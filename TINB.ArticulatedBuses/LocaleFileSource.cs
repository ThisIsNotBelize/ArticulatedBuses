using System.Collections.Generic;
using Colossal;

namespace TINB.ArticulatedBuses
{
    /* Generic IDictionarySource backed by a fixed key->string map. Hands the
       prepared entries to the game's localization manager. */
    public sealed class LocaleFileSource : IDictionarySource
    {
        private readonly IReadOnlyDictionary<string, string> m_Entries;

        public LocaleFileSource(IReadOnlyDictionary<string, string> entries)
        {
            m_Entries = entries;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return m_Entries;
        }

        public void Unload()
        {
        }
    }
}
