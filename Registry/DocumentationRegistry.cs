using System;
using System.Collections.Generic;
using System.Linq;
using GenericModDocumentationFramework.Models;

namespace GenericModDocumentationFramework.Registry
{

    public class DocumentationRegistry
    {
        private readonly Dictionary<string, ModDocumentation> _mods = new(StringComparer.OrdinalIgnoreCase);


        public void RegisterFromJson(string uniqueId, ModDocumentation doc)
        {
            _mods[uniqueId] = doc;
        }


        public bool IsRegistered(string uniqueId) => _mods.ContainsKey(uniqueId);


        public ModDocumentation? GetDocumentation(string uniqueId) =>
            _mods.TryGetValue(uniqueId, out var doc) ? doc : null;


        public IReadOnlyList<ModDocumentation> GetAllMods() =>
            _mods.Values.OrderBy(m => m.GetName()).ToList();


        public bool HasAnyMods => _mods.Count > 0;
    }
}
