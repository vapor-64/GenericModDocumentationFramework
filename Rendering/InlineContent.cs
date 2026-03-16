using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

namespace GenericModDocumentationFramework.Rendering
{

    public class InlineSegment
    {
        public string?         Text     { get; }
        public ParsedItemData? ItemData { get; }
        public bool            IsSprite => ItemData != null;

        private InlineSegment(string text)         { Text = text; }
        private InlineSegment(ParsedItemData item) { ItemData = item; }

        public static InlineSegment FromText(string text)         => new(text);
        public static InlineSegment FromItem(ParsedItemData item) => new(item);

        public static float MeasureLineWidth(List<InlineSegment> line, SpriteFont font, int spriteSize)
        {
            float w = 0f;
            foreach (var seg in line)
            {
                if (seg.IsSprite)
                    w += spriteSize + 2;
                else if (!string.IsNullOrEmpty(seg.Text))
                    w += font.MeasureString(seg.Text).X;
            }
            return w;
        }
    }


    public static class InlineParser
    {
        private static readonly Regex TokenPattern =
            new(@"\[(\([A-Za-z]+\))?([A-Za-z0-9_]+)\]", RegexOptions.Compiled);

        public static List<List<InlineSegment>> WrapRich(
            string text, SpriteFont font, int maxWidth, int spriteSize)
        {
            var result = new List<List<InlineSegment>>();
            if (string.IsNullOrEmpty(text)) return result;

            foreach (var rawLine in text.Split('\n'))
            {
                var atoms = Tokenize(rawLine);
                WrapAtoms(atoms, font, maxWidth, spriteSize, result);
            }

            return result;
        }

        private static List<InlineSegment> Tokenize(string line)
        {
            var atoms  = new List<InlineSegment>();
            int cursor = 0;

            foreach (Match m in TokenPattern.Matches(line))
            {
                if (m.Index > cursor)
                    SplitWords(line.Substring(cursor, m.Index - cursor), atoms);

                string qualifier = m.Groups[1].Value;
                string itemId    = m.Groups[2].Value;
                string qualId    = string.IsNullOrEmpty(qualifier)
                    ? "(O)" + itemId
                    : qualifier + itemId;

                var data = ItemRegistry.GetDataOrErrorItem(qualId);
                atoms.Add(InlineSegment.FromItem(data));

                cursor = m.Index + m.Length;
            }

            if (cursor < line.Length)
                SplitWords(line.Substring(cursor), atoms);

            return atoms;
        }

        private static void SplitWords(string fragment, List<InlineSegment> atoms)
        {
            string[] parts = fragment.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                string word = i < parts.Length - 1 ? parts[i] + " " : parts[i];
                if (word.Length > 0)
                    atoms.Add(InlineSegment.FromText(word));
            }
        }

        private static void WrapAtoms(
            List<InlineSegment>       atoms,
            SpriteFont                font,
            int                       maxWidth,
            int                       spriteSize,
            List<List<InlineSegment>> result)
        {
            var   currentLine  = new List<InlineSegment>();
            float currentWidth = 0f;

            foreach (var atom in atoms)
            {
                float atomW = MeasureAtom(atom, font, spriteSize);

                if (currentWidth + atomW > maxWidth && currentLine.Count > 0)
                {
                    TrimTrailingSpace(currentLine);
                    result.Add(currentLine);
                    currentLine  = new List<InlineSegment>();
                    currentWidth = 0f;

                    if (!atom.IsSprite && atom.Text != null)
                    {
                        string trimmed = atom.Text.TrimStart(' ');
                        if (trimmed.Length > 0)
                        {
                            currentLine.Add(InlineSegment.FromText(trimmed));
                            currentWidth = font.MeasureString(trimmed).X;
                        }
                        continue;
                    }
                }

                currentLine.Add(atom);
                currentWidth += atomW;
            }

            if (currentLine.Count > 0)
            {
                TrimTrailingSpace(currentLine);
                result.Add(currentLine);
            }
            else if (atoms.Count == 0)
            {
                result.Add(new List<InlineSegment>());
            }
        }

        private static float MeasureAtom(InlineSegment atom, SpriteFont font, int spriteSize)
        {
            if (atom.IsSprite) return spriteSize + 2;
            return string.IsNullOrEmpty(atom.Text) ? 0f : font.MeasureString(atom.Text).X;
        }

        private static void TrimTrailingSpace(List<InlineSegment> line)
        {
            if (line.Count == 0) return;
            var last = line[line.Count - 1];
            if (!last.IsSprite && last.Text != null && last.Text.EndsWith(" "))
            {
                string trimmed = last.Text.TrimEnd(' ');
                line[line.Count - 1] = trimmed.Length > 0
                    ? InlineSegment.FromText(trimmed)
                    : InlineSegment.FromText("");
            }
        }
    }
}
