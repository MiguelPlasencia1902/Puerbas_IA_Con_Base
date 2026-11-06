using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CapriDesktop
{
    // The only in-process route to CAPRI Music. OAuth and HTTP remain in the existing helper.
    internal sealed class MusicBridge
    {
        private readonly string helper;
        private readonly Func<string, string, string> invoke;
        private readonly Func<string, string, string, string> youtube;
        private readonly object gate = new object();
        private ToolArguments pending;
        private string waiting;
        private DateTime waitingUntil;
        private volatile string lastProvider;
        private int busy;

        internal MusicBridge(string modsFolder, Func<string, string, string> testInvoker = null, Func<string, string, string, string> testYouTube = null)
        {
            helper = Path.Combine(modsFolder, "capri-music-data", "helper", "CapriMusicHelper.exe");
            invoke = testInvoker ?? InvokeHelper;
            youtube = testYouTube ?? Hands.PlayMusicLegacy;
        }

        internal bool MatchesReply(string text)
        {
            lock (gate)
            {
                if (waiting == null) return false;
                if (DateTime.UtcNow > waitingUntil) { Clear(); return false; }
                text = (text ?? "").Trim();
                bool matched = waiting == "provider"
                    ? Regex.IsMatch(text, @"^(?:(?:en|mejor\s+en)\s+)?(?:spotify|youtube)[.!?]?$", RegexOptions.IgnoreCase)
                    : Regex.IsMatch(text, @"^(?:la\s+primera|el\s+primero|\d{1,2}|(?:en\s+)?mi\s+computadora|(?:selecciona|elige|usa)\s+(?:el\s+)?dispositivo\s+.{1,100})[.!?]?$", RegexOptions.IgnoreCase);
                if (!matched) Clear();
                return matched;
            }
        }

        internal string Reply(string text)
        {
            ToolArguments saved;
            string stage;
            lock (gate) { saved = pending; stage = waiting; Clear(); }
            if (saved == null) return "Ya no tengo una selección musical pendiente.";
            if (stage == "provider")
            {
                saved.App = Regex.IsMatch(text ?? "", @"youtube", RegexOptions.IgnoreCase) ? "youtube" : "spotify";
                return Run("play_music", saved);
            }
            string name = (text ?? "").Trim().TrimEnd('.', '!', '?');
            if (Regex.IsMatch(name, @"^(la primera|el primero)$", RegexOptions.IgnoreCase)) name = "1";
            else if (Regex.IsMatch(name, @"^(?:en\s+)?mi computadora$", RegexOptions.IgnoreCase)) name = "computadora";
            else name = Regex.Replace(name, @"^(?:selecciona|elige|usa)\s+(?:el\s+)?dispositivo\s+", "", RegexOptions.IgnoreCase);
            string selected = Run("spotify_music", new ToolArguments { Action = "select_device", Name = name });
            if (selected.StartsWith("Usaré", StringComparison.Ordinal)) return selected + " " + Run("spotify_music", saved);
            lock (gate) Hold("device", saved);
            return selected;
        }

        internal string Run(string tool, ToolArguments args)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return "Ya estoy atendiendo una orden de Spotify. Espera un momento.";
            try
            {
                string result = Execute(tool, args);
                if (tool == "spotify_music" && args != null && args.Action == "select_device" && result.StartsWith("Usaré", StringComparison.Ordinal))
                {
                    ToolArguments saved = null;
                    lock (gate) if (waiting == "device") { saved = pending; Clear(); }
                    if (saved != null) result += " " + Execute("spotify_music", saved);
                }
                return result;
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        private string Execute(string tool, ToolArguments args)
        {
            if (args == null) return "Faltan los datos de la orden musical.";
            bool structured = tool == "spotify_music";
            if (tool == "play_music")
            {
                if (string.IsNullOrWhiteSpace(args.Song)) return "¿Qué quieres escuchar?";
                if (args.App == null)
                {
                    lock (gate) Hold("provider", new ToolArguments { Song = args.Song, Kind = args.Kind });
                    return "¿Quieres que la reproduzca en Spotify o en YouTube?";
                }
                if (args.App == "youtube")
                {
                    lastProvider = "youtube";
                    return youtube(args.Song, "youtube", args.Kind);
                }
                if (args.App != "spotify") return "Elige Spotify o YouTube.";
                args = new ToolArguments { Action = args.Kind == "playlist" ? "play_playlist" : args.Kind == "artist" ? "play_artist" : args.Kind == "album" ? "unsupported" : Regex.IsMatch(args.Song.Trim(), @"^(?:música|music|algo de música|something|some music)$", RegexOptions.IgnoreCase) ? "resume" : "play_track", Song = args.Song, Name = args.Song };
            }
            if (tool != "spotify_music" && tool != "play_music") return "Orden musical no reconocida.";
            if ((!Hands.Valid("spotify_music", args) && args.Action != "play_artist" && args.Action != "unsupported") || Regex.IsMatch((args.Song ?? "") + (args.Artist ?? "") + (args.Name ?? ""), "[\\r\\n\\x00]")) return "La orden de Spotify tiene argumentos inválidos.";
            if (args.Action == "unsupported") return "Todavía no puedo reproducir álbumes mediante CAPRI Music.";
            string command, value = "";
            switch (args.Action)
            {
                case "search": command = "search-v2"; value = Structured(args); break;
                case "play_track": command = structured ? "track-v2" : "track"; value = structured ? Structured(args) : Query(args); break;
                case "play_artist": command = "artist"; value = args.Song; break;
                case "play_playlist": command = "playlist"; value = PlaylistName(args.Name); break;
                case "devices": command = "devices"; break;
                case "select_device": command = "device"; value = args.Name; break;
                case "pause": case "resume": case "next": case "previous": case "current": case "playlists": command = args.Action; break;
                case "find_playlist": command = "find-playlist"; value = PlaylistName(args.Name); break;
                default: return "Orden de Spotify no permitida.";
            }
            if ((command == "playlist" || command == "find-playlist") && string.IsNullOrWhiteSpace(value)) return "¿Cómo se llama la playlist?";
            if (value != null && value.Length > 200) return "La búsqueda musical es demasiado larga.";
            string result = invoke(command, value ?? "");
            if (command == "track" || command == "track-v2" || command == "artist" || command == "playlist" || command == "resume" || command == "pause" || command == "next" || command == "previous") lastProvider = "spotify";
            if ((command == "track" || command == "track-v2" || command == "artist" || command == "playlist" || command == "play" || command == "resume") && (result.StartsWith("Selecciona primero el dispositivo", StringComparison.OrdinalIgnoreCase) || result.StartsWith("Tu dispositivo cambió", StringComparison.OrdinalIgnoreCase)))
            {
                string devices = invoke("devices", "");
                lock (gate) Hold("device", args);
                return result + " " + devices + " Elige el dispositivo y continuaré la petición.";
            }
            return result;
        }

        internal bool RoutesMedia(ToolArguments args)
        {
            if (args == null || !Regex.IsMatch(args.Action ?? "", @"\A(pause|resume|next|previous|now_playing)\z")) return false;
            return args.App == "spotify" || args.App == null && lastProvider == "spotify";
        }

        internal string Media(ToolArguments args) { return Run("spotify_music", new ToolArguments { Action = args.Action == "now_playing" ? "current" : args.Action }); }

        internal static bool Mentioned(string request, string value)
        {
            return !string.IsNullOrWhiteSpace(request) && !string.IsNullOrWhiteSpace(value)
                && System.Globalization.CultureInfo.GetCultureInfo("es").CompareInfo.IndexOf(request, value.Trim(), System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
        }

        private static string Query(ToolArguments args) { return args.Song.Trim() + (string.IsNullOrWhiteSpace(args.Artist) ? "" : " de " + args.Artist.Trim()); }
        private static string Structured(ToolArguments args) { return args.Song.Trim() + "\n" + (args.Artist == null ? "" : args.Artist.Trim()); }
        private static string PlaylistName(string name) { return Regex.Replace(name.Trim(), @"^(?:(?:mi|mis|my)\s+)?playlist\s+(?:de\s+)?", "", RegexOptions.IgnoreCase); }
        private void Hold(string stage, ToolArguments args) { pending = args; waiting = stage; waitingUntil = DateTime.UtcNow.AddMinutes(5); }
        private void Clear() { pending = null; waiting = null; }

        private string InvokeHelper(string command, string value)
        {
            if (!File.Exists(helper)) return "Falta el auxiliar de CAPRI Music en la carpeta de mods.";
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(helper) };
                using (Process process = Process.Start(info))
                {
                    process.StandardInput.WriteLine(command);
                    process.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                    process.StandardInput.Close();
                    if (!process.WaitForExit(30000)) { process.Kill(); return "Spotify tardó demasiado. Inténtalo otra vez."; }
                    string answer = process.StandardOutput.ReadToEnd().Trim();
                    return answer.Length == 0 ? "El auxiliar de Spotify no respondió." : answer.Length > 2000 ? answer.Substring(0, 2000) : answer;
                }
            }
            catch (Exception) { return "No pude comunicarme con el auxiliar de Spotify."; }
        }
    }
}
