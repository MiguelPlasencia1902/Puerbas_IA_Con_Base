using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using CapriDesktop;

namespace CapriDesktop
{
    public interface ICapriHost
    {
        string Folder { get; }
        void Say(string line);
        void Log(string line);
        Bitmap Art(string name);
        void PlayIntro();
        void Sound(string name);
    }
    public interface ICapriMod
    {
        void Start(ICapriHost capri);
        bool Heard(string words);
        void Stop();
    }
}

internal sealed class FakeHost : ICapriHost
{
    public string Folder { get { return Path.Combine(Path.GetTempPath(), "capri-music-mod-test"); } }
    public readonly List<string> Said = new List<string>();
    public void Say(string text) { lock (Said) Said.Add(text); }
    public void Log(string line) { }
    public Bitmap Art(string name) { return null; }
    public void PlayIntro() { }
    public void Sound(string name) { }
}

internal static class ModTests
{
    private static void Check(bool yes, string label) { if (!yes) throw new Exception(label); }
    public static void Main()
    {
        FakeHost host = new FakeHost();
        CapriMusic mod = new CapriMusic();
        mod.Start(host);
        foreach (string ordinary in new[] { "Hola, ¿cómo estás?", "Abre YouTube", "Reproduce música en YouTube", "Reproduce esto", "Capri, reproduce un vídeo", "Pausa la música", "¿Qué canción está sonando?", "Continúa con lo que decías", "Siguiente paso", "Háblame de Spotify", "¿Te gusta Daft Punk?", "Capri, selecciona dispositivo DESKTOP-004F4HA", "Capri, reproduce Never Fade Away de Samurai", "Capri, pon música de Daft Punk", "Capri, siguiente canción", "Capri, busca mis playlists" })
            Check(!mod.Heard(ordinary), "consumió charla: " + ordinary);
        foreach (string command in new[] { "Capri, conecta Spotify", "Capri, permisos de Spotify", "Capri, estado de Spotify", "Capri, desconecta Spotify" })
            Check(mod.Heard(command), "no reconoció: " + command);
        mod.Stop();
        Console.WriteLine("Reconocimiento y no interferencia: correctos.");
    }
}
