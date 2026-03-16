using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GenericModDocumentationFramework.Models.Entries
{

    public enum DividerStyle
    {
        Single,
        Double,
        Dotted,
        IconCentered
    }



    public class SectionTitleEntry : IDocumentationEntry
    {
        public EntryType    Type      => EntryType.SectionTitle;
        public Alignment    Alignment { get; }
        public Func<string> GetText   { get; }

        public int?         FontSize  { get; }

        public SectionTitleEntry(Func<string> getText, Alignment alignment = Alignment.Left, int? fontSize = null)
        {
            GetText   = getText;
            Alignment = alignment;
            FontSize  = fontSize;
        }
    }

    public class ParagraphEntry : IDocumentationEntry
    {
        public EntryType    Type      => EntryType.Paragraph;
        public Alignment    Alignment { get; }
        public Func<string> GetText   { get; }

        public int?         FontSize  { get; }

        public ParagraphEntry(Func<string> getText, Alignment alignment = Alignment.Left, int? fontSize = null)
        {
            GetText   = getText;
            Alignment = alignment;
            FontSize  = fontSize;
        }
    }


    public class CaptionEntry : IDocumentationEntry
    {
        public EntryType    Type      => EntryType.Caption;
        public Alignment    Alignment { get; }
        public Func<string> GetText   { get; }

        public int?         FontSize  { get; }

        public CaptionEntry(Func<string> getText, Alignment alignment = Alignment.Center, int? fontSize = null)
        {
            GetText   = getText;
            Alignment = alignment;
            FontSize  = fontSize;
        }
    }



    public class HeaderImageEntry
    {
        private readonly Func<Texture2D> _getTexture;
        private Texture2D?               _cachedTexture;

        public Rectangle? SourceRect { get; }

        public HeaderImageEntry(Func<Texture2D> getTexture, Rectangle? sourceRect)
        {
            _getTexture = getTexture;
            SourceRect  = sourceRect;
        }

        public Texture2D GetTexture() => _cachedTexture ??= _getTexture();
    }



    public class ImageEntry : IDocumentationEntry
    {
        private readonly Func<Texture2D> _getTexture;
        private Texture2D?               _cachedTexture;

        public EntryType  Type       => EntryType.Image;
        public Alignment  Alignment  { get; }
        public Rectangle? SourceRect { get; }
        public double     Scale      { get; }
        public IReadOnlyList<Func<string>>? Items { get; }

        public const int Gutter       = 16;
        public const int BulletIndent = 28;

        public ImageEntry(
            Func<Texture2D>              getTexture,
            Rectangle?                   sourceRect,
            double                       scale,
            Alignment                    alignment = Alignment.Left,
            IReadOnlyList<Func<string>>? items     = null)
        {
            _getTexture = getTexture;
            SourceRect  = sourceRect;
            Scale       = Math.Max(0.01, scale);
            Alignment   = alignment;
            Items       = items;
        }

        public Texture2D GetTexture() => _cachedTexture ??= _getTexture();

        public bool HasFloatLayout => Items is { Count: > 0 } && Alignment is Alignment.Left or Alignment.Right;
    }



    public class KeyValuePairEntry : IDocumentationEntry
    {
        public EntryType    Type     => EntryType.KeyValuePair;
        public Func<string> GetKey   { get; }
        public Func<string> GetValue { get; }

        public int?         FontSize { get; }

        public KeyValuePairEntry(Func<string> getKey, Func<string> getValue, int? fontSize = null)
        {
            GetKey   = getKey;
            GetValue = getValue;
            FontSize = fontSize;
        }
    }



    public class DividerEntry : IDocumentationEntry
    {
        public EntryType    Type  => EntryType.Divider;
        public DividerStyle Style { get; }

        public DividerEntry(DividerStyle style = DividerStyle.Single) { Style = style; }
    }



    public class SpacerEntry : IDocumentationEntry
    {
        public EntryType Type   => EntryType.Spacer;
        public int       Height { get; }

        public SpacerEntry(int height) { Height = Math.Max(1, height); }
    }



    public class SpoilerEntry : IDocumentationEntry
    {
        public EntryType    Type       => EntryType.Spoiler;
        public Func<string> GetLabel   { get; }
        public Func<string> GetContent { get; }
        public bool         IsRevealed { get; set; } = false;

        public const int HeaderHeight = 32;

        public SpoilerEntry(Func<string> getLabel, Func<string> getContent)
        {
            GetLabel   = getLabel;
            GetContent = getContent;
        }
    }



    public class ListEntry : IDocumentationEntry
    {
        public EntryType                   Type      => EntryType.List;
        public Alignment                   Alignment { get; }
        public IReadOnlyList<Func<string>> Items     { get; }
        public bool                        IsOrdered { get; }

        public int?                        FontSize  { get; }

        public const int BulletIndent = 28;

        public ListEntry(IReadOnlyList<Func<string>> items, bool isOrdered, Alignment alignment = Alignment.Left, int? fontSize = null)
        {
            Items     = items;
            IsOrdered = isOrdered;
            Alignment = alignment;
            FontSize  = fontSize;
        }
    }



    public class LinkEntry : IDocumentationEntry
    {
        public EntryType    Type      => EntryType.Link;
        public Alignment    Alignment { get; }
        public Func<string> GetLabel  { get; }
        public string       Url       { get; }

        public LinkEntry(Func<string> getLabel, string url, Alignment alignment = Alignment.Left)
        {
            GetLabel  = getLabel;
            Url       = url;
            Alignment = alignment;
        }

        public void Open()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = Url,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }



    public class GifEntry : IDocumentationEntry
    {
        private readonly Func<Texture2D> _getTexture;
        private Texture2D?               _cachedTexture;
        private Rectangle[]?             _frames;

        public EntryType  Type          => EntryType.Gif;
        public Alignment  Alignment     { get; }
        public int        FrameCount    { get; }
        public double     FrameDuration { get; }
        public double     Scale         { get; }

        public int    CurrentFrame   { get; set; } = 0;
        public double ElapsedSeconds { get; set; } = 0.0;

        public int Columns { get; }
        public int Rows    { get; }

        public GifEntry(
            Func<Texture2D> getTexture,
            int             frameCount,
            double          frameDuration,
            double          scale     = 1.0,
            Alignment       alignment = Alignment.Left,
            int             columns   = 0,
            int             rows      = 1)
        {
            _getTexture   = getTexture;
            FrameCount    = Math.Max(1, frameCount);
            FrameDuration = Math.Max(0.016, frameDuration);
            Scale         = Math.Max(0.01, scale);
            Alignment     = alignment;

            if (columns <= 0)
            {
                Columns = FrameCount;
                Rows    = 1;
            }
            else
            {
                Columns = columns;
                Rows    = Math.Max(1, rows);
            }
        }

        public Texture2D GetTexture() => _cachedTexture ??= _getTexture();

        public Rectangle GetFrameRect(int frameIndex)
        {
            if (_frames == null)
                BuildFrames();
            return _frames![Math.Clamp(frameIndex, 0, _frames.Length - 1)];
        }

        private void BuildFrames()
        {
            var tex = GetTexture();
            int fw  = tex.Width  / Columns;
            int fh  = tex.Height / Rows;
            _frames = new Rectangle[FrameCount];
            for (int i = 0; i < FrameCount; i++)
            {
                int col = i % Columns;
                int row = i / Columns;
                _frames[i] = new Rectangle(col * fw, row * fh, fw, fh);
            }
        }

        public (int w, int h) GetScaledSize()
        {
            var tex = GetTexture();
            int fw  = tex.Width  / Columns;
            int fh  = tex.Height / Rows;
            return ((int)Math.Round(fw * Scale), (int)Math.Round(fh * Scale));
        }
    }



    public class RowEntry : IDocumentationEntry
    {
        public EntryType Type => EntryType.Row;

        public double LeftFraction { get; }

        public const int ColumnGap = 16;

        public IReadOnlyList<IDocumentationEntry> LeftEntries  { get; }
        public IReadOnlyList<IDocumentationEntry> RightEntries { get; }

        public RowEntry(
            IReadOnlyList<IDocumentationEntry> leftEntries,
            IReadOnlyList<IDocumentationEntry> rightEntries,
            double leftFraction = 0.5)
        {
            LeftEntries  = leftEntries;
            RightEntries = rightEntries;
            LeftFraction = Math.Clamp(leftFraction, 0.05, 0.95);
        }
    }
}
