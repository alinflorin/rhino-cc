using Rhino;
using RhinoCC.Core.Conversations;

namespace RhinoCC.Chat
{
    /// <summary>Conversations live in the .3dm itself, in the document's user strings.</summary>
    internal sealed class DocStringStore : IKeyValueStore
    {
        private readonly RhinoDoc _doc;

        public DocStringStore(RhinoDoc doc)
        {
            _doc = doc;
        }

        public string Get(string key) => _doc.Strings.GetValue(key);

        public void Set(string key, string value) => _doc.Strings.SetString(key, value);

        public void Delete(string key) => _doc.Strings.Delete(key);
    }
}
