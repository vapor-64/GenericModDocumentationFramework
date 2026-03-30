using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace GenericModDocumentationFramework.Models
{

    public class ModDocumentation
    {

        public string      UniqueId { get; }


        public Func<string> GetName  { get; }


        public DocumentationPage DefaultPage { get; } = new(null, () => "Overview");


        private readonly List<DocumentationPage> _extraPages = new();


        private readonly Dictionary<string, DocumentationPage> _pageById = new(StringComparer.OrdinalIgnoreCase);


        private IReadOnlyList<DocumentationPage>? _allPagesCache;

        public ModDocumentation(string uniqueId, Func<string> getName)
        {
            UniqueId = uniqueId;
            GetName  = getName;
        }


        public void AddPage(string pageId, Func<string> getPageName)
        {
            if (_pageById.ContainsKey(pageId))
                return;

            var page = new DocumentationPage(pageId, getPageName);
            _extraPages.Add(page);
            _pageById[pageId] = page;
            _allPagesCache    = null;
        }


        public DocumentationPage? GetPage(string pageId)
        {
            return _pageById.TryGetValue(pageId, out var page) ? page : null;
        }


        public IReadOnlyList<DocumentationPage> GetAllPages()
        {
            if (_allPagesCache != null) return _allPagesCache;

            var pages = new List<DocumentationPage>();
            if (DefaultPage.Entries.Count > 0 || _extraPages.Count == 0)
                pages.Add(DefaultPage);
            pages.AddRange(_extraPages);

            _allPagesCache = pages;
            return _allPagesCache;
        }
    }
}
