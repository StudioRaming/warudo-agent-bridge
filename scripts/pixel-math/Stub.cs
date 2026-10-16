using System;

// Minimal stand-ins for the few UnityEngine types Pixmap.cs and PixelDiff.cs touch, so their math runs under plain .NET.
namespace UnityEngine
{
    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public enum TextureFormat { RGBA32 }

    public class Object
    {
        public static void Destroy(Object o) { }
    }

    public class Texture2D : Object
    {
        readonly Color32[] data;
        public int width, height;
        public Texture2D(int w, int h, TextureFormat f, bool mips) { width = w; height = h; data = new Color32[w * h]; }
        public Color32[] GetPixels32() { return (Color32[])data.Clone(); }
        public void SetPixels32(Color32[] c) { Array.Copy(c, data, c.Length); }
        public void Apply(bool a, bool b) { }
        public byte[] EncodeToPNG() { return new byte[] { 1 }; }
    }
}
