using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CapriMusicHelper;

internal static class Program
{
    internal const string Redirect = "http://127.0.0.1:8888/callback";
    internal const string Scope = "user-read-playback-state user-modify-playback-state playlist-read-private user-library-read";
    internal static string Home => Environment.CurrentDirectory;
    internal static string ClientFile => Path.Combine(Home, "client-id.txt");
    internal static string TokenFile => Path.Combine(Home, "spotify-tokens.dpapi");
    internal static string DeviceFile => Path.Combine(Home, "device-preference.txt");
    internal static string DeviceListFile => Path.Combine(Home, "device-list.json");

    private static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") { Console.WriteLine(SelfTest.Run()); return; }
        string? command = Console.ReadLine();
        string encoded = Console.ReadLine() ?? "";
        string value;
        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch { Console.WriteLine("La orden de Spotify no es válida."); return; }
        if (command == null) { Console.WriteLine("No recibí una orden de Spotify."); return; }
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var spotify = new Spotify(client);
            Console.WriteLine(await spotify.Run(command, value));
        }
        catch (SpotifyProblem e) { Console.WriteLine(e.Message); }
        catch (TaskCanceledException) { Console.WriteLine("Spotify tardó demasiado en responder."); }
        catch (HttpRequestException) { Console.WriteLine("No pude conectar con Spotify. Revisa la red."); }
        catch (IOException) { Console.WriteLine("No pude acceder a los datos locales de CAPRI Music."); }
        catch (CryptographicException) { Console.WriteLine("No pude abrir los tokens protegidos de esta cuenta de Windows. Vuelve a conectar Spotify."); }
        catch (Exception) { Console.WriteLine("CAPRI Music encontró un error local."); }
    }
}

internal sealed class SpotifyProblem(string message) : Exception(message);

internal sealed class Tokens
{
    public string Access { get; set; } = "";
    public string Refresh { get; set; } = "";
    public string Scope { get; set; } = "";
    public DateTimeOffset Expires { get; set; }
}

internal static class SecureStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob data, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob protectedData);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob plainData);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    internal static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    internal static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        Blob input = new() { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            bool ok = protect
                ? CryptProtectData(ref input, "CAPRI Music Spotify", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!ok) throw new CryptographicException("DPAPI");
            try { byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    internal static Tokens? Load()
    {
        if (!File.Exists(Program.TokenFile)) return null;
        byte[] plain = Unprotect(File.ReadAllBytes(Program.TokenFile));
        try { return JsonSerializer.Deserialize<Tokens>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static void Save(Tokens tokens)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        byte[] encrypted;
        try { encrypted = Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temp = Program.TokenFile + ".new";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, Program.TokenFile, true);
    }
    internal static void Delete() { if (File.Exists(Program.TokenFile)) File.Delete(Program.TokenFile); }
}

internal static class Pkce
{
    internal static string Random(int bytes = 48) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static bool StateMatches(string? expected, string? actual)
    {
        if (expected == null || actual == null) return false;
        byte[] a = Encoding.ASCII.GetBytes(expected), b = Encoding.ASCII.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record Device(string Id, string Name, string Type, bool Active, bool Restricted);
internal sealed record MusicItem(string Name, string Artist, string Uri, bool Playable);
internal sealed record Playlist(string Name, string Uri);

internal sealed class Spotify(HttpClient http)
{
    private const string ApiRoot = "https://api.spotify.com/v1";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    internal Tokens? tokens;

    internal async Task<string> Run(string command, string value) => command switch
    {
        "connect" => await Connect(),
        "disconnect" => Disconnect(),
        "status" => await Status(),
        "permissions" => Permissions(),
        "devices" => await DevicesText(),
        "device" => await SelectDevice(value),
        "play" or "resume" => await Control("play"),
        "pause" => await Control("pause"),
        "next" => await Control("next"),
        "previous" => await Control("previous"),
        "current" => await Current(),
        "track" => await SearchAndPlay(value, false),
        "track-v2" => await SearchStructured(value, false),
        "search" => await SearchAndPlay(value, false, true),
        "search-v2" => await SearchStructured(value, true),
        "artist" => await SearchAndPlay(value, true),
        "playlists" => await PlaylistsText(),
        "find-playlist" => await FindPlaylist(value),
        "playlist" => await PlayPlaylist(value),
        "saved" => await Saved(),
        _ => "No conozco esa orden de Spotify."
    };

    private static string ClientId()
    {
        if (!File.Exists(Program.ClientFile)) throw new SpotifyProblem("Falta client-id.txt en la carpeta de datos de CAPRI Music.");
        string id = File.ReadAllText(Program.ClientFile).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-fA-F0-9]{32}$")) throw new SpotifyProblem("El Client ID local no tiene el formato esperado.");
        return id;
    }

    private static string Form(params (string Key, string Value)[] fields) => string.Join("&", fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));

    private async Task<string> Connect()
    {
        string clientId = ClientId();
        string verifier = Pkce.Random(), state = Pkce.Random(32);
        string url = "https://accounts.spotify.com/authorize?" + Form(
            ("response_type", "code"), ("client_id", clientId), ("redirect_uri", Program.Redirect),
            ("scope", Program.Scope), ("code_challenge_method", "S256"),
            ("code_challenge", Pkce.Challenge(verifier)), ("state", state));
        using var listener = new TcpListener(IPAddress.Loopback, 8888);
        try { listener.Start(); }
        catch (SocketException) { throw new SpotifyProblem("El puerto 8888 está ocupado. Cierra el programa que lo usa y vuelve a intentar."); }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        string? code = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (code == null)
        {
            TcpClient socket;
            try { socket = await listener.AcceptTcpClientAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new SpotifyProblem("La autorización de Spotify caducó o se canceló."); }
            using (socket)
            {
                if (!IPAddress.IsLoopback(((IPEndPoint)socket.Client.RemoteEndPoint!).Address)) continue;
                socket.ReceiveTimeout = 5000;
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string? request = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                string? hostHeader = null;
                for (int i = 0; i < 40; i++)
                {
                    string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (string.IsNullOrEmpty(line)) break;
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) hostHeader = line.Substring(5).Trim();
                }
                if (hostHeader != "127.0.0.1:8888" || request == null || !request.StartsWith("GET /callback?", StringComparison.Ordinal)) { await Respond(stream, 400, "Solicitud no válida."); continue; }
                string target = request.Split(' ')[1];
                var query = ParseQuery(new Uri("http://127.0.0.1:8888" + target).Query);
                query.TryGetValue("state", out string? received);
                if (!Pkce.StateMatches(state, received)) { await Respond(stream, 400, "Estado no válido. Vuelve a CAPRI Music."); continue; }
                if (query.ContainsKey("error")) { await Respond(stream, 200, "Autorización cancelada. Puedes cerrar esta pestaña."); throw new SpotifyProblem("No autorizaste a CAPRI Music en Spotify."); }
                if (!query.TryGetValue("code", out code) || string.IsNullOrWhiteSpace(code)) { await Respond(stream, 400, "Falta el código de autorización."); code = null; continue; }
                await Respond(stream, 200, "Spotify autorizado. Puedes cerrar esta pestaña.");
            }
        }
        using JsonDocument answer = await TokenRequest(Form(("client_id", clientId), ("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Program.Redirect), ("code_verifier", verifier)));
        SaveTokens(answer.RootElement, null);
        return "Spotify quedó conectado. Ahora puedes elegir un dispositivo o pedir una canción.";
    }

    private static async Task Respond(Stream stream, int status, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes("<html><meta charset=\"utf-8\"><body>" + message + "</body></html>");
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Bad Request") + "\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head); await stream.WriteAsync(body);
    }
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            result[Uri.UnescapeDataString(pair[0])] = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
        }
        return result;
    }
    private async Task<JsonDocument> TokenRequest(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new SpotifyProblem("Spotify rechazó la autorización. Vuelve a conectar la cuenta.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    private void SaveTokens(JsonElement data, string? oldRefresh)
    {
        string access = data.GetProperty("access_token").GetString() ?? "";
        string refresh = Text(data, "refresh_token") ?? oldRefresh ?? "";
        int seconds = data.GetProperty("expires_in").GetInt32();
        if (access.Length == 0 || refresh.Length == 0) throw new SpotifyProblem("Spotify no entregó una sesión renovable.");
        tokens = new Tokens { Access = access, Refresh = refresh, Scope = Text(data, "scope") ?? tokens?.Scope ?? "", Expires = DateTimeOffset.UtcNow.AddSeconds(seconds) };
        SecureStore.Save(tokens);
    }
    private async Task<string> Access()
    {
        tokens ??= SecureStore.Load();
        if (tokens == null) throw new SpotifyProblem("Spotify no está conectado. Di «Capri, conecta Spotify».");
        if (tokens.Expires > DateTimeOffset.UtcNow.AddMinutes(1)) return tokens.Access;
        using JsonDocument answer = await TokenRequest(Form(("grant_type", "refresh_token"), ("refresh_token", tokens.Refresh), ("client_id", ClientId())));
        SaveTokens(answer.RootElement, tokens.Refresh);
        return tokens!.Access;
    }
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.True;
    private static IEnumerable<JsonElement> Items(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Array ? child.EnumerateArray() : [];
    private static string SafeName(string name) => new string(name.Where(c => !char.IsControl(c)).Take(60).ToArray());
    private static string PlayerPath(string action, Device device) => "/me/player/" + action + (device.Id.Length == 0 ? "" : "?device_id=" + Uri.EscapeDataString(device.Id));

    private async Task<JsonDocument?> Call(HttpMethod method, string path, string? body = null, bool retry = true)
    {
        string access = await Access();
        using var request = new HttpRequestMessage(method, ApiRoot + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized && retry)
        {
            tokens!.Expires = DateTimeOffset.MinValue;
            await Access();
            return await Call(method, path, body, false);
        }
        if (!response.IsSuccessStatusCode) throw await ApiError(response);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        string content = await response.Content.ReadAsStringAsync();
        return content.Length == 0 ? null : JsonDocument.Parse(content);
    }
    private static async Task<SpotifyProblem> ApiError(HttpResponseMessage response)
    {
        string? reason = null, detail = null;
        try
        {
            string body = response.Content == null ? "" : await response.Content.ReadAsStringAsync();
            if (body.Length <= 4096)
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    string? candidate = Text(error, "reason");
                    if (candidate != null && System.Text.RegularExpressions.Regex.IsMatch(candidate, "^[A-Z_]{1,48}$")) reason = candidate;
                    string? serverMessage = Text(error, "message");
                    if (serverMessage is "Refused" or "Premium required" or "Restriction violated") detail = serverMessage;
                }
            }
        }
        catch (JsonException) { }
        string diagnostic = " (HTTP " + (int)response.StatusCode + (reason == null ? "" : ", " + reason) + (detail == null ? "" : ", " + detail) + ")";
        string message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "La sesión venció o fue revocada. Vuelve a conectar Spotify.",
            HttpStatusCode.Forbidden when reason == "QUOTA_EXCEEDED" => "La aplicación alcanzó una cuota de Spotify Development Mode.",
            HttpStatusCode.Forbidden => "Spotify rechazó esta orden. Comprueba permisos concedidos, cuenta Premium, acceso a la app en Development Mode y dispositivo activo; el motivo exacto requiere verificación en Spotify.",
            HttpStatusCode.NotFound => "Spotify no encontró ese contenido o dispositivo.",
            HttpStatusCode.TooManyRequests when reason == "QUOTA_EXCEEDED" => "La aplicación alcanzó su cuota de Spotify Development Mode.",
            HttpStatusCode.TooManyRequests => "Spotify pidió esperar antes de hacer más solicitudes.",
            HttpStatusCode.BadRequest => "Spotify no pudo usar ese dispositivo o contenido.",
            _ when (int)response.StatusCode >= 500 => "Spotify tiene un problema temporal. Inténtalo después.",
            _ => "Spotify no pudo completar esa operación."
        };
        return new SpotifyProblem(message + diagnostic);
    }

    private string Disconnect()
    {
        SecureStore.Delete(); tokens = null;
        if (File.Exists(Program.DeviceFile)) File.Delete(Program.DeviceFile);
        if (File.Exists(Program.DeviceListFile)) File.Delete(Program.DeviceListFile);
        return "Desconecté Spotify de CAPRI Music. Para revocar también el permiso en Spotify, quita CAPRI Music en la página de aplicaciones de tu cuenta.";
    }
    private async Task<string> Status()
    {
        if (!File.Exists(Program.TokenFile)) return "Spotify no está conectado. Di «Capri, conecta Spotify».";
        try { await Access(); return "Spotify está conectado. No iniciaré reproducción hasta que me lo pidas."; }
        catch (SpotifyProblem) { return "La sesión de Spotify necesita una nueva autorización."; }
    }
    private string Permissions()
    {
        Tokens? saved = SecureStore.Load();
        if (saved == null) return "Spotify no está conectado.";
        if (saved.Scope.Length == 0) return "Esta sesión no conservó el listado de permisos concedidos. Una nueva vinculación permitirá comprobarlos; no hace falta para consultar dispositivos.";
        string[] granted = saved.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] required = Program.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] missing = required.Except(granted, StringComparer.Ordinal).ToArray();
        return "Permisos concedidos: " + string.Join(", ", granted) + "." + (missing.Length == 0 ? " Están los cuatro permisos solicitados." : " Faltan: " + string.Join(", ", missing) + ". Vuelve a vincular Spotify.");
    }
    private async Task<List<Device>> Devices()
    {
        using var doc = await Call(HttpMethod.Get, "/me/player/devices");
        return doc == null ? [] : Items(doc.RootElement, "devices").Select(x => new Device(Text(x, "id") ?? "", Text(x, "name") ?? "", Text(x, "type") ?? "", Bool(x, "is_active"), Bool(x, "is_restricted"))).ToList();
    }
    private async Task<string> DevicesText()
    {
        var devices = await Devices();
        if (devices.Count == 0) return "No veo dispositivos de Spotify. Abre Spotify Desktop y reproduce algo allí una vez.";
        File.WriteAllText(Program.DeviceListFile, JsonSerializer.Serialize(devices));
        return "Dispositivos: " + string.Join("; ", devices.Select((d, i) => (i + 1) + ". " + d.Name + " (" + d.Type + (d.Restricted ? ", restringido" : d.Id.Length == 0 ? d.Active ? ", activo sin ID" : ", sin ID; actívalo en Spotify" : d.Active ? ", activo" : "") + ")")) + ". Di «selecciona dispositivo 1» o su nombre.";
    }
    private async Task<string> SelectDevice(string name)
    {
        var available = await Devices();
        name = name.Trim();
        List<Device> matches;
        if (int.TryParse(name, out int number))
        {
            if (!File.Exists(Program.DeviceListFile)) return "Primero pide la lista de dispositivos para seleccionar por número.";
            var listed = JsonSerializer.Deserialize<List<Device>>(File.ReadAllText(Program.DeviceListFile)) ?? [];
            if (number < 1 || number > listed.Count) return "Ese número no está en la última lista de dispositivos.";
            Device wanted = listed[number - 1];
            matches = available.Where(d => d.Id == wanted.Id && d.Name == wanted.Name && d.Type == wanted.Type).ToList();
            if (matches.Count == 0) return "La lista de dispositivos cambió. Pídela de nuevo antes de seleccionar.";
        }
        else if (name.Equals("computadora", StringComparison.OrdinalIgnoreCase) || name.Equals("mi computadora", StringComparison.OrdinalIgnoreCase)) matches = available.Where(d => d.Type.Equals("computer", StringComparison.OrdinalIgnoreCase)).ToList();
        else matches = available.Where(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return "No coincide el nombre «" + SafeName(name) + "» con los dispositivos actuales. Los números 0 y las letras O no se sustituyen. Pide la lista y selecciona su número.";
        if (matches.Count > 1) return "Hay varios dispositivos con ese nombre. Necesito una selección más precisa.";
        if (matches[0].Restricted) return "Ese dispositivo figura como restringido y Spotify no acepta órdenes para él.";
        if (matches[0].Id.Length == 0 && !matches[0].Active) return "Ese dispositivo no tiene ID y no está activo. Actívalo manualmente en Spotify y selecciónalo otra vez.";
        File.WriteAllLines(Program.DeviceFile, [matches[0].Id, matches[0].Name, matches[0].Type]);
        return "Usaré «" + matches[0].Name + "» para Spotify. No he iniciado reproducción.";
    }
    private async Task<Device> PlaybackDevice()
    {
        var devices = (await Devices()).Where(d => !d.Restricted).ToList();
        if (devices.Count == 0) throw new SpotifyProblem("No hay un dispositivo Spotify compatible. Abre Spotify Desktop y vuelve a intentar.");
        string[] preferred = File.Exists(Program.DeviceFile) ? File.ReadAllLines(Program.DeviceFile) : [];
        if (preferred.Length != 3) throw new SpotifyProblem("Selecciona primero el dispositivo Spotify donde quieres escuchar música.");
        var chosen = devices.Where(d => d.Id == preferred[0] && d.Name == preferred[1] && d.Type == preferred[2] && (d.Id.Length > 0 || d.Active)).ToList();
        if (chosen.Count == 1) return chosen[0];
        throw new SpotifyProblem("Tu dispositivo cambió o ya no está disponible. Pide la lista y selecciónalo otra vez.");
    }
    private async Task<string> Control(string action)
    {
        Device device = await PlaybackDevice();
        string path = PlayerPath(action, device);
        await Call(action is "play" or "pause" ? HttpMethod.Put : HttpMethod.Post, path);
        return action switch
        {
            "play" => await CurrentConfirmation("Pedí a Spotify que continuara."),
            "pause" => await CurrentConfirmation("Pedí a Spotify que pausara."),
            "next" => await CurrentConfirmation("Pedí la siguiente canción."),
            _ => await CurrentConfirmation("Pedí la canción anterior.")
        };
    }
    private async Task<string> CurrentConfirmation(string fallback)
    {
        await Task.Delay(450);
        try { return await Current(); }
        catch (SpotifyProblem problem) { return fallback + " No pude confirmar el estado: " + problem.Message; }
    }
    private async Task<string> Current()
    {
        using var doc = await Call(HttpMethod.Get, "/me/player");
        if (doc == null) return "Spotify no informa ninguna reproducción activa.";
        var root = doc.RootElement;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return "Spotify no informa una canción en este momento.";
        string title = Text(item, "name") ?? "una canción";
        string artists = string.Join(", ", Items(item, "artists").Select(a => Text(a, "name")).Where(a => a != null));
        return (Bool(root, "is_playing") ? "Está sonando " : "Está pausada ") + title + (artists.Length > 0 ? " de " + artists : "") + ".";
    }
    private static string Normalize(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
    }
    private async Task<string> SearchStructured(string value, bool searchOnly)
    {
        string[] fields = value.Split('\n');
        if (fields.Length != 2 || string.IsNullOrWhiteSpace(fields[0]) || fields[0].Length > 160 || fields[1].Length > 160 || value.Contains('\r')) throw new SpotifyProblem("La búsqueda musical tiene argumentos inválidos.");
        return await SearchAndPlay(fields[0], false, searchOnly, fields[1]);
    }

    private async Task<string> SearchAndPlay(string requested, bool artistOnly, bool searchOnly = false, string? explicitArtist = null)
    {
        requested = requested.Trim();
        string? artist = null, title = requested;
        if (artistOnly) { artist = requested; title = ""; }
        else if (explicitArtist != null) artist = explicitArtist.Length == 0 ? null : explicitArtist;
        else
        {
            var split = System.Text.RegularExpressions.Regex.Match(requested, @"^(.+?)\s+de\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (split.Success) { title = split.Groups[1].Value; artist = split.Groups[2].Value; }
        }
        string query = artistOnly ? "artist:" + artist : title + (artist == null ? "" : " artist:" + artist);
        using var doc = await Call(HttpMethod.Get, "/search?type=track&limit=10&q=" + Uri.EscapeDataString(query));
        var tracks = new List<MusicItem>();
        if (doc != null && doc.RootElement.TryGetProperty("tracks", out var container))
            foreach (var item in Items(container, "items"))
            {
                string name = Text(item, "name") ?? "", uri = Text(item, "uri") ?? "";
                string artists = string.Join(", ", Items(item, "artists").Select(a => Text(a, "name")).Where(a => a != null));
                bool playable = !item.TryGetProperty("is_playable", out var flag) || flag.ValueKind != JsonValueKind.False;
                if (uri.StartsWith("spotify:track:", StringComparison.Ordinal) && playable) tracks.Add(new(name, artists, uri, playable));
            }
        if (tracks.Count == 0) return "No encontré una canción disponible que coincida.";
        if (searchOnly) return "Encontré: " + string.Join("; ", tracks.Take(5).Select(t => t.Name + " de " + t.Artist)) + ". No inicié reproducción.";
        var matches = tracks.Where(t => (artistOnly || Normalize(t.Name) == Normalize(title)) && (artist == null || t.Artist.Split(',').Any(a => Normalize(a) == Normalize(artist)))).ToList();
        if (matches.Count == 0) return "Encontré resultados, pero ninguno coincide claramente. Dime título y artista exactos.";
        if (!artistOnly && matches.Select(t => t.Uri).Distinct().Count() > 1 && matches.Select(t => Normalize(t.Artist)).Distinct().Count() > 1)
            return "Hay varias coincidencias para esa canción. Añade el artista para elegir bien.";
        MusicItem selected = matches[0];
        Device device = await PlaybackDevice();
        await Call(HttpMethod.Put, PlayerPath("play", device), JsonSerializer.Serialize(new { uris = new[] { selected.Uri } }));
        return await ConfirmTarget(selected.Uri, null, "Pedí reproducir «" + selected.Name + "» de " + selected.Artist + ". Aún no pude confirmarlo.");
    }
    private async Task<List<Playlist>> Playlists()
    {
        var all = new List<Playlist>();
        for (int offset = 0; offset < 200; offset += 50)
        {
            using var doc = await Call(HttpMethod.Get, "/me/playlists?limit=50&offset=" + offset);
            if (doc == null) break;
            var page = Items(doc.RootElement, "items").Select(x => new Playlist(Text(x, "name") ?? "", Text(x, "uri") ?? "")).Where(x => x.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)).ToList();
            all.AddRange(page);
            if (Text(doc.RootElement, "next") == null) break;
        }
        return all;
    }
    private async Task<string> PlaylistsText()
    {
        var list = await Playlists();
        return list.Count == 0 ? "No encontré playlists en tu biblioteca." : "Tus playlists incluyen: " + string.Join(", ", list.Take(12).Select(x => x.Name)) + (list.Count > 12 ? ". Hay más; di el nombre de la que buscas." : ".");
    }
    private async Task<string> FindPlaylist(string name)
    {
        var list = (await Playlists()).Where(x => Normalize(x.Name).Contains(Normalize(name))).Take(10).ToList();
        return list.Count == 0 ? "No encontré una playlist con ese nombre en tu biblioteca." : "Encontré: " + string.Join(", ", list.Select(x => x.Name)) + ".";
    }
    private async Task<string> PlayPlaylist(string name)
    {
        var list = await Playlists();
        var matches = list.Where(x => Normalize(x.Name) == Normalize(name)).ToList();
        if (matches.Count == 0) matches = list.Where(x => Normalize(x.Name).Contains(Normalize(name))).ToList();
        if (matches.Count == 0) return "No encontré esa playlist en tu biblioteca.";
        if (matches.Count > 1) return "Hay varias playlists parecidas: " + string.Join(", ", matches.Take(5).Select(x => x.Name)) + ". Dime el nombre completo.";
        Device device = await PlaybackDevice();
        await Call(HttpMethod.Put, PlayerPath("play", device), JsonSerializer.Serialize(new { context_uri = matches[0].Uri }));
        return await ConfirmTarget(null, matches[0].Uri, "Pedí reproducir la playlist «" + matches[0].Name + "». Aún no pude confirmarlo.");
    }
    private async Task<string> ConfirmTarget(string? trackUri, string? contextUri, string pending)
    {
        await Task.Delay(600);
        try
        {
            using var doc = await Call(HttpMethod.Get, "/me/player");
            if (doc == null || !Bool(doc.RootElement, "is_playing")) return pending;
            var root = doc.RootElement;
            if (trackUri != null && root.TryGetProperty("item", out var item) && Text(item, "uri") == trackUri)
                return "Confirmado: está sonando «" + (Text(item, "name") ?? "la canción") + "» en Spotify.";
            if (contextUri != null && root.TryGetProperty("context", out var context) && Text(context, "uri") == contextUri)
                return "Confirmado: Spotify está reproduciendo esa playlist.";
            return pending;
        }
        catch (SpotifyProblem problem) { return pending + " La comprobación falló: " + problem.Message; }
    }
    private async Task<string> Saved()
    {
        using var doc = await Call(HttpMethod.Get, "/me/tracks?limit=10");
        if (doc == null) return "No pude consultar las canciones guardadas.";
        var names = new List<string>();
        foreach (var x in Items(doc.RootElement, "items"))
            if (x.TryGetProperty("track", out var track) && Text(track, "name") is string title) names.Add(title);
        return names.Count == 0 ? "No encontré canciones guardadas." : "Entre tus canciones guardadas están: " + string.Join(", ", names) + ".";
    }
}

internal static class SelfTest
{
    internal static string Run()
    {
        string verifier = Pkce.Random(), state = Pkce.Random(32);
        if (verifier.Length < 43 || verifier.Length > 128 || Pkce.Challenge(verifier).Length != 43) throw new Exception("PKCE");
        if (!Pkce.StateMatches(state, state) || Pkce.StateMatches(state, Pkce.Random(32))) throw new Exception("state");
        byte[] source = Encoding.UTF8.GetBytes("prueba-local");
        byte[] encrypted = SecureStore.Protect(source), decrypted = SecureStore.Unprotect(encrypted);
        if (encrypted.SequenceEqual(source) || !decrypted.SequenceEqual(source)) throw new Exception("DPAPI");
        TestApi().GetAwaiter().GetResult();
        return "Pruebas locales: PKCE, state, DPAPI, persistencia, renovación, selección de dispositivos y HTTP 403/404/429 correctos.";
    }

    private static async Task TestApi()
    {
        string before = Environment.CurrentDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "capri-music-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Environment.CurrentDirectory = temp;
            File.WriteAllText(Program.ClientFile, new string('a', 32));
            var handler = new FakeHandler();
            using var client = new HttpClient(handler);
            var spotify = new Spotify(client) { tokens = new Tokens { Access = "old-test", Refresh = "refresh-test", Expires = DateTimeOffset.MinValue } };
            string devices = await spotify.Run("devices", "");
            if (!devices.Contains("Escritorio", StringComparison.Ordinal) || handler.RefreshCalls != 1) throw new Exception("renovación/dispositivos");
            if (!File.Exists(Program.TokenFile)) throw new Exception("persistencia");
            byte[] saved = File.ReadAllBytes(Program.TokenFile);
            if (Encoding.UTF8.GetString(saved).Contains("new-test", StringComparison.Ordinal)) throw new Exception("token sin cifrar");
            if (SecureStore.Load()?.Access != "new-test") throw new Exception("lectura DPAPI");
            string selected = await spotify.Run("device", "Escritorio");
            if (!selected.Contains("Usaré", StringComparison.Ordinal) || File.ReadAllLines(Program.DeviceFile).Length != 3) throw new Exception("preferencia");
            string now = await spotify.Run("current", "");
            if (!now.Contains("Prueba", StringComparison.Ordinal)) throw new Exception("estado simulado");
            string searched = await spotify.Run("search", "Prueba");
            if (!searched.Contains("Prueba de Artista", StringComparison.Ordinal) || !searched.Contains("No inicié reproducción", StringComparison.Ordinal)) throw new Exception("búsqueda sin reproducción");
            string structured = await spotify.Run("search-v2", "Canción de Hielo\nArtista");
            if (!structured.Contains("No inicié reproducción", StringComparison.Ordinal)) throw new Exception("búsqueda estructurada");
            if (!(await spotify.Run("permissions", "")).Contains("user-modify-playback-state", StringComparison.Ordinal)) throw new Exception("permisos concedidos");
            handler.NullableDesktop = true;
            string listing = await spotify.Run("devices", "");
            if (!listing.Contains("DESKTOP-004F4HA", StringComparison.Ordinal) || !listing.Contains("activo sin ID", StringComparison.Ordinal)) throw new Exception("listado sin ID");
            string mistaken = await spotify.Run("device", "DESKTOP-OO4F4HA");
            if (!mistaken.Contains("números 0", StringComparison.Ordinal)) throw new Exception("confusión cero/O");
            if (!(await spotify.Run("device", "DESKTOP-004F4HA")).Contains("DESKTOP-004F4HA", StringComparison.Ordinal)) throw new Exception("selección por nombre con ID nulo");
            if (!(await spotify.Run("device", "1")).Contains("DESKTOP-004F4HA", StringComparison.Ordinal)) throw new Exception("selección numerada");
            if (!(await spotify.Run("device", "mi computadora")).Contains("DESKTOP-004F4HA", StringComparison.Ordinal)) throw new Exception("selección computadora");
            handler.DesktopActive = false;
            if (!(await spotify.Run("device", "DESKTOP-004F4HA")).Contains("no está activo", StringComparison.Ordinal)) throw new Exception("ID nulo inactivo");
            handler.DesktopActive = true;
            handler.RefusePlayback = true;
            try { await spotify.Run("pause", ""); throw new Exception("403 aceptado"); }
            catch (SpotifyProblem problem)
            {
                if (!problem.Message.Contains("HTTP 403", StringComparison.Ordinal) || !problem.Message.Contains("Refused", StringComparison.Ordinal) || !handler.PlaybackWithoutId) throw;
            }
            handler.QuotaPlayback = true;
            try { await spotify.Run("pause", ""); throw new Exception("cuota 403 aceptada"); }
            catch (SpotifyProblem problem) { if (!problem.Message.Contains("HTTP 429, QUOTA_EXCEEDED", StringComparison.Ordinal)) throw; }
            handler.QuotaPlayback = false;
            handler.NotFoundPlayback = true;
            try { await spotify.Run("pause", ""); throw new Exception("404 aceptado"); }
            catch (SpotifyProblem problem) { if (!problem.Message.Contains("HTTP 404", StringComparison.Ordinal)) throw; }
            handler.NotFoundPlayback = false;
            handler.RefusePlayback = false;
            handler.RefuseConfirmation = true;
            string unconfirmed = await spotify.Run("track", "Prueba de Artista");
            if (!unconfirmed.Contains("Aún no pude confirmarlo", StringComparison.Ordinal) || !unconfirmed.Contains("HTTP 403, Refused", StringComparison.Ordinal) || unconfirmed.Contains("Confirmado", StringComparison.Ordinal)) throw new Exception("reproducción aceptada pero comprobación rechazada");
            handler.RefuseConfirmation = false;
            try { await spotify.Run("saved", ""); throw new Exception("429 aceptado"); }
            catch (SpotifyProblem problem) { if (!problem.Message.Contains("esperar", StringComparison.Ordinal)) throw; }
            await spotify.Run("disconnect", "");
            if (File.Exists(Program.TokenFile) || File.Exists(Program.DeviceFile) || File.Exists(Program.DeviceListFile)) throw new Exception("desconexión");
        }
        finally
        {
            Environment.CurrentDirectory = before;
            Directory.Delete(temp, true);
        }
    }
    private sealed class FakeHandler : HttpMessageHandler
    {
        internal int RefreshCalls;
        internal bool NullableDesktop, DesktopActive = true, RefusePlayback, RefuseConfirmation, QuotaPlayback, NotFoundPlayback, PlaybackWithoutId;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            HttpResponseMessage response;
            if (path == "/api/token")
            {
                RefreshCalls++;
                response = Reply(HttpStatusCode.OK, "{\"access_token\":\"new-test\",\"refresh_token\":\"refresh-new\",\"scope\":\"user-read-playback-state user-modify-playback-state playlist-read-private user-library-read\",\"expires_in\":3600}");
            }
            else if (request.Headers.Authorization?.Parameter != "new-test") response = Reply(HttpStatusCode.Unauthorized, "{}");
            else if (path == "/v1/me/player/devices") response = NullableDesktop
                ? Reply(HttpStatusCode.OK, "{\"devices\":[{\"id\":null,\"name\":\"DESKTOP-004F4HA\",\"type\":\"Computer\",\"is_active\":" + (DesktopActive ? "true" : "false") + ",\"is_restricted\":false},{\"id\":\"speaker-test\",\"name\":\"Echo Dot di Camila\",\"type\":\"Speaker\",\"is_active\":false,\"is_restricted\":false}]}")
                : Reply(HttpStatusCode.OK, "{\"devices\":[{\"id\":\"device-test\",\"name\":\"Escritorio\",\"type\":\"computer\",\"is_active\":true,\"is_restricted\":false}]}");
            else if (path == "/v1/me/player/pause" && NotFoundPlayback) response = Reply(HttpStatusCode.NotFound, "{\"error\":{\"status\":404,\"message\":\"Not found\"}}");
            else if (path == "/v1/me/player/pause" && QuotaPlayback) response = Reply(HttpStatusCode.TooManyRequests, "{\"error\":{\"status\":429,\"message\":\"Too many requests\",\"reason\":\"QUOTA_EXCEEDED\"}}");
            else if (path == "/v1/me/player/pause" && RefusePlayback)
            {
                PlaybackWithoutId = string.IsNullOrEmpty(request.RequestUri?.Query);
                response = Reply(HttpStatusCode.Forbidden, "{\"error\":{\"status\":403,\"message\":\"Refused\"}}");
            }
            else if (path == "/v1/search") response = Reply(HttpStatusCode.OK, "{\"tracks\":{\"items\":[{\"name\":\"Prueba\",\"uri\":\"spotify:track:test\",\"is_playable\":true,\"artists\":[{\"name\":\"Artista\"}]}]}}");
            else if (path == "/v1/me/player/play") response = Reply(HttpStatusCode.NoContent, "");
            else if (path == "/v1/me/player" && RefuseConfirmation) response = Reply(HttpStatusCode.Forbidden, "{\"error\":{\"status\":403,\"message\":\"Refused\"}}");
            else if (path == "/v1/me/player") response = Reply(HttpStatusCode.OK, "{\"is_playing\":true,\"item\":{\"name\":\"Prueba\",\"artists\":[{\"name\":\"Artista\"}]}}");
            else if (path == "/v1/me/tracks") response = Reply(HttpStatusCode.TooManyRequests, "{}");
            else response = Reply(HttpStatusCode.NotFound, "{}");
            return Task.FromResult(response);
        }
        private static HttpResponseMessage Reply(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
