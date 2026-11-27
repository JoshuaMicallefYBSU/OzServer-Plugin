using System.Runtime.CompilerServices;

namespace OzServerPlugin;

// A set, by reference, that does not keep its members alive. For remembering which of vatSys's
// flight records something has already been done to.
//
// A HashSet<FDP2.FDR> holds on to every record put in it, and vatSys drops its own without saying
// so in ways a set cannot follow: a finished flight is deleted ten minutes later, and a disconnect
// empties the list with no event at all (FDP2.ClearFDRs). A record is not small - vatSys keeps a
// predicted position for every third of a mile of its route, a megabyte or more for a long-haul
// flight, and its strip and track hang off it - so each one a set kept was held for the rest of
// the session. Here a record vatSys has let go of is collected, and leaves the set with it.
//
// Not synchronised beyond what ConditionalWeakTable does itself: Add is a check and then an
// insert, so callers hold their own lock, as they did around the HashSet.
sealed class WeakSet<T> where T : class
{
    static readonly object Present = new();

    ConditionalWeakTable<T, object> _members = new();

    // False when it was already there.
    public bool Add(T item)
    {
        if (_members.TryGetValue(item, out _))
            return false;
        _members.Add(item, Present);
        return true;
    }

    public bool Contains(T item) => _members.TryGetValue(item, out _);

    public bool Remove(T item) => _members.Remove(item);

    // ConditionalWeakTable has no Clear on .NET Framework.
    public void Clear() => _members = new ConditionalWeakTable<T, object>();
}
