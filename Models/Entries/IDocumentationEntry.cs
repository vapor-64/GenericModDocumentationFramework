namespace GenericModDocumentationFramework.Models.Entries
{
    public interface IDocumentationEntry
    {
        EntryType Type { get; }
    }

    /// <summary>
    /// Implemented by entries that can serve as named anchor targets for internal links.
    /// </summary>
    public interface IAnchorable
    {
        /// <summary>
        /// The anchor ID for this entry, or <c>null</c> if this entry is not a target.
        /// Must match the <c>anchor</c> field used by an <see cref="InternalLinkEntry"/>.
        /// </summary>
        string? Anchor { get; }
    }
}
