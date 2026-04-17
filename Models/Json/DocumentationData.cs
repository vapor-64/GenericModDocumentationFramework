using System.Collections.Generic;

namespace GenericModDocumentationFramework.Models.Json
{

    public class DocumentationData
    {
        public int     Format  { get; set; }
        public string  ModName { get; set; } = "";
        public List<PageData> Pages { get; set; } = new();
    }

    public class PageData
    {
        public string? Id   { get; set; }
        public string  Name { get; set; } = "Overview";
        public ImageRefData? HeaderImage { get; set; }
        public List<EntryData> Entries { get; set; } = new();
    }

    public class EntryData
    {

        public string  Type  { get; set; } = "";
        public string? Align { get; set; }

        public string? Text { get; set; }

        public int? FontSize { get; set; }

        public string?   Texture    { get; set; }
        public RectData? SourceRect { get; set; }
        public double    Scale      { get; set; } = 2;

        public List<string>? Items { get; set; }

        public string? Key   { get; set; }
        public string? Value { get; set; }

        public string? Style { get; set; }

        public int Height { get; set; } = 16;

        public string? Url   { get; set; }

        public string? Label { get; set; }

        public List<EntryData>? Left         { get; set; }
        public List<EntryData>? Right        { get; set; }
        public double           LeftFraction { get; set; } = 0.5;

        public List<EntryData>? Entries      { get; set; }
        public int              Indent       { get; set; } = 32;
        public bool             ShowRule     { get; set; } = true;

        public int    FrameCount    { get; set; } = 1;
        public double FrameDuration { get; set; } = 0.1;
        public int    Columns       { get; set; } = 0;
        public int    Rows          { get; set; } = 1;
    }

    public class ImageRefData
    {
        public string    Texture    { get; set; } = "";
        public RectData? SourceRect { get; set; }
    }

    public class RectData
    {
        public int X      { get; set; }
        public int Y      { get; set; }
        public int Width  { get; set; }
        public int Height { get; set; }
    }
}
