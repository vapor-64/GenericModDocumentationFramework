using System;
using System.Collections.Generic;
using GenericModDocumentationFramework.Models.Entries;

namespace GenericModDocumentationFramework.Models
{

    public class DocumentationPage
    {

        public string? PageId { get; }


        public Func<string> GetPageName { get; }


        public HeaderImageEntry? HeaderImage { get; set; }


        public List<IDocumentationEntry> Entries { get; } = new();

        public DocumentationPage(string? pageId, Func<string> getPageName)
        {
            PageId = pageId;
            GetPageName = getPageName;
        }
    }
}
