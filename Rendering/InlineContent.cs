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
        public string?         EmoteName { get; }

        /// <summary>True when this segment is a Stardew item sprite, resolved via ItemRegistry.</summary>
        public bool IsSprite => ItemData != null;

        /// <summary>True when this segment is a custom framework emote, resolved via EmoteRegistry.</summary>
        public bool IsEmote  => EmoteName != null;

        private InlineSegment(string text)                    { Text      = text; }
        private InlineSegment(ParsedItemData item)            { ItemData  = item; }
        private InlineSegment(string emoteName, bool isEmote) { EmoteName = emoteName; }

        public static InlineSegment FromText(string text)          => new(text);
        public static InlineSegment FromItem(ParsedItemData item)  => new(item);
        public static InlineSegment FromEmote(string emoteName)    => new(emoteName, true);

        public static float MeasureLineWidth(List<InlineSegment> line, SpriteFont font, int spriteSize)
        {
            float w = 0f;
            foreach (var seg in line)
            {
                if (seg.IsSprite || seg.IsEmote)
                    w += spriteSize + 2;
                else if (!string.IsNullOrEmpty(seg.Text))
                    w += font.MeasureString(seg.Text).X;
            }
            return w;
        }
    }


    public static class InlineParser
    {
        // Matches Stardew item sprite tokens: [128] or [(O)128] or [(BC)12] etc.
        // Item ID must be numeric — this deliberately excludes word-based tokenizable
        // string tokens like [FarmName] which are resolved earlier by TokenParser.
        private static readonly Regex ItemTokenPattern =
            new(@"\[(\([A-Za-z]+\))?(\d+)\]", RegexOptions.Compiled);

        // Matches custom framework emote tokens: {heart} {star} etc.
        // Name must be one or more word characters (letters, digits, underscore).
        // Deliberately distinct from item tokens ([...]) and i18n tokens ({{...}}).
        private static readonly Regex EmoteTokenPattern =
            new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

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
            var atoms = new List<InlineSegment>();

            // Merge both token patterns into a single left-to-right pass so that
            // item tokens and emote tokens interleave correctly with plain text.
            // We build a combined match list sorted by position.
            var allMatches = new List<(int index, int length, InlineSegment segment)>();

            foreach (Match m in ItemTokenPattern.Matches(line))
            {
                string qualifier = m.Groups[1].Value;
                string itemId    = m.Groups[2].Value;
                string qualId    = string.IsNullOrEmpty(qualifier)
                    ? "(O)" + itemId
                    : qualifier + itemId;

                var data = ItemRegistry.GetDataOrErrorItem(qualId);
                allMatches.Add((m.Index, m.Length, InlineSegment.FromItem(data)));
            }

            foreach (Match m in EmoteTokenPattern.Matches(line))
            {
                string emoteName = m.Groups[1].Value;

                // Only treat as an emote token if the name is actually registered.
                // Unrecognised {tokens} are left as plain text so they don't silently disappear.
                if (EmoteRegistry.IsRegistered(emoteName))
                    allMatches.Add((m.Index, m.Length, InlineSegment.FromEmote(emoteName)));
            }

            // Sort all matches by their position in the source string.
            allMatches.Sort((a, b) => a.index.CompareTo(b.index));

            int cursor = 0;
            foreach (var (index, length, segment) in allMatches)
            {
                // Plain text between the previous match and this one.
                if (index > cursor)
                    SplitWords(line.Substring(cursor, index - cursor), atoms);

                atoms.Add(segment);
                cursor = index + length;
            }

            // Any remaining plain text after the last match.
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

                    if (!atom.IsSprite && !atom.IsEmote && atom.Text != null)
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
            if (atom.IsSprite || atom.IsEmote) return spriteSize + 2;
            return string.IsNullOrEmpty(atom.Text) ? 0f : font.MeasureString(atom.Text).X;
        }

        private static void TrimTrailingSpace(List<InlineSegment> line)
        {
            if (line.Count == 0) return;
            var last = line[line.Count - 1];
            if (!last.IsSprite && !last.IsEmote && last.Text != null && last.Text.EndsWith(" "))
            {
                string trimmed = last.Text.TrimEnd(' ');
                line[line.Count - 1] = trimmed.Length > 0
                    ? InlineSegment.FromText(trimmed)
                    : InlineSegment.FromText("");
            }
        }
    }
}
