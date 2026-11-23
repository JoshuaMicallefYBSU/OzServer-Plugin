using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using vatsys;

namespace OzServerPlugin;

// Makes vatSys's own SELCAL window accept and transmit SELCAL32 codes, not just SELCAL16.
//
// SELCAL32 (ICAO Annex 10 Vol III, Amendment 91) adds sixteen tones, T-Z and 1-9, to the original
// A-S set. Each new tone sits geometrically halfway between two of the originals (T between A and B,
// U between B and C, ...), with 9 one half-step above S. Codes keep their old shape: two pairs,
// "AB-CD", any of the 32 tones.
//
// Rather than replacing the window, this patches the two things inside vatSys that stop a SELCAL32
// code getting through. Every other part of the window then works unchanged: the SEL/ remark lookup,
// the callsign search, the TX interlock and the FSD "SELCAL AB-CD" message.
//
//  1. The window's validation. SELCALWindow.CheckValidSelcal rejects any character for which
//     `c => !"ABCDEFGHJKLMPQRS".Contains(c)` is true. The string is an inlined ldstr, so changing
//     SELCALWindow.VALID_SELCAL_CHARS (a const) would do nothing. The lambdas are compiler-cached,
//     though: CheckValidSelcal does `<>c.<>9__N ?? (<>c.<>9__N = new Func<char,bool>(...))`, and
//     those cache fields are ordinary writable statics. Seeding them with our own predicate means
//     vatSys's code calls ours instead and never builds its own.
//
//  2. The AFV tone generator. vatsys.Audio.TransmitSELCAL hands the code to
//     GeoVR.Client.UserClient.TransmitSELCAL, then to SelcalInput.Play. Play throws "Invalid SELCAL
//     tone" for any character missing from the private static Dictionary<char, float> codeLookup, and
//     AddSignalsToMixer reads each tone's frequency from that same dictionary. It holds only the
//     sixteen SELCAL16 tones, so the new ones are added to it. Nothing else writes to it, and it is
//     only read during a transmission, so adding to it once at load needs no locking.
//
// Both are reflection into compiler-generated or private members. Neither patch is applied unless
// what it finds still looks exactly like what is described above. If vatSys or AFV change shape,
// the window simply stays SELCAL16 and the reason is logged, rather than half-working.
//
// The window is patched only if the tones were, so the window never accepts a code that AFV would
// then refuse to play.
//
// Pilot side, out of our hands: the aircraft only reacts if the pilot's client matches the received
// code against its own SELCAL setting, and a client that only lets pilots enter SELCAL16 codes can
// never match a SELCAL32 one.
static class Selcal32
{
    // ICAO Annex 10 Vol III SELCAL32 extension. The SELCAL16 tones are AFV's own, left as they are.
    static readonly (char Tone, float Hz)[] AddedTones =
    {
        ('T', 329.2f), ('U', 365.2f), ('V', 405.0f), ('W', 449.3f),
        ('X', 498.3f), ('Y', 552.7f), ('Z', 613.1f), ('1', 680.0f),
        ('2', 754.2f), ('3', 836.6f), ('4', 927.9f), ('5', 1029.2f),
        ('6', 1141.6f), ('7', 1266.2f), ('8', 1404.4f), ('9', 1557.8f),
    };

    const string Selcal16Tones = "ABCDEFGHJKLMPQRS";
    static readonly HashSet<char> ValidTones = new((Selcal16Tones + new string(AddedTones.Select(t => t.Tone).ToArray())).ToCharArray());

    // Same contract as vatSys's own lambda: true means the character is NOT a valid tone.
    static readonly Func<char, bool> IsInvalidTone = c => !ValidTones.Contains(c);

    static bool _applied;

    public static void Apply()
    {
        if (_applied)
            return;
        _applied = true;

        try
        {
            if (!ExtendAfvTones(out var toneReason))
            {
                ActionLog.Log("SELCAL", $"SELCAL32 not enabled, AFV tone table not patched: {toneReason}");
                return;
            }

            if (!PatchWindowValidation(out var windowReason))
            {
                ActionLog.Log("SELCAL", $"SELCAL32 tones added to AFV but the SELCAL window was not patched: {windowReason}");
                return;
            }

            ActionLog.Log("SELCAL", "SELCAL32 enabled: the SELCAL window accepts T-Z and 1-9");
        }
        catch (Exception ex)
        {
            // Never let this take the rest of the plugin down with it: SELCAL16 still works without it.
            ActionLog.Log("SELCAL", $"SELCAL32 not enabled: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static bool ExtendAfvTones(out string reason)
    {
        // Resolved by name from vatSys's own bin folder. The plugin does not reference GeoVR.Client,
        // and has no need to.
        var selcalInput = Type.GetType("GeoVR.Client.SelcalInput, GeoVR.Client", throwOnError: false);
        if (selcalInput == null)
        {
            reason = "GeoVR.Client.SelcalInput not found";
            return false;
        }

        var field = selcalInput.GetField("codeLookup", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not Dictionary<char, float> lookup)
        {
            reason = "SelcalInput.codeLookup missing or not a Dictionary<char, float>";
            return false;
        }

        // Expect exactly the SELCAL16 table, or one we (or a newer AFV) already extended. Anything
        // else means the table is not what this code thinks it is.
        if (!Selcal16Tones.All(lookup.ContainsKey))
        {
            reason = $"codeLookup does not hold the SELCAL16 tones (has: {new string(lookup.Keys.OrderBy(k => k).ToArray())})";
            return false;
        }

        foreach (var (tone, hz) in AddedTones)
        {
            // Respect a value AFV already defines itself.
            if (!lookup.ContainsKey(tone))
                lookup[tone] = hz;
        }

        reason = "";
        return true;
    }

    static bool PatchWindowValidation(out string reason)
    {
        var closure = typeof(Audio).Assembly.GetType("vatsys.SELCALWindow+<>c", throwOnError: false);
        if (closure == null)
        {
            reason = "vatsys.SELCALWindow+<>c not found";
            return false;
        }

        const BindingFlags Statics = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        const BindingFlags Instances = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

        // The cache field names carry compiler-assigned ordinals (<>9__7_0) that a vatSys rebuild can
        // renumber. So they are found from the lambda bodies instead: each "<CheckValidSelcal>b__X_Y"
        // method is cached in "<>9__X_Y".
        const string LambdaPrefix = "<CheckValidSelcal>b__";
        var lambdas = closure.GetMethods(Instances)
            .Where(m => m.Name.StartsWith(LambdaPrefix, StringComparison.Ordinal)
                        && m.ReturnType == typeof(bool)
                        && m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(char))
            .ToList();
        if (lambdas.Count == 0)
        {
            reason = "no CheckValidSelcal lambdas found";
            return false;
        }

        var instance = closure.GetField("<>9", Statics)?.GetValue(null);
        if (instance == null)
        {
            reason = "SELCALWindow+<>c.<>9 not found";
            return false;
        }

        var caches = new List<FieldInfo>();
        foreach (var lambda in lambdas)
        {
            var cacheName = "<>9__" + lambda.Name.Substring(LambdaPrefix.Length);
            var cache = closure.GetField(cacheName, Statics);
            if (cache == null || cache.FieldType != typeof(Func<char, bool>) || cache.IsInitOnly)
            {
                reason = $"cache field {cacheName} missing or not a writable Func<char, bool>";
                return false;
            }

            // Only swap in a predicate that is a strict superset of the one being replaced: vatSys's
            // must still accept every SELCAL16 tone and reject everything else. If it has started
            // checking something new, leave it alone.
            bool Invalid(char c) => (bool)lambda.Invoke(instance, new object[] { c });
            if (Selcal16Tones.Any(Invalid) || !"INOTZ19".All(Invalid))
            {
                reason = $"{lambda.Name} no longer behaves as the SELCAL16 character check";
                return false;
            }

            caches.Add(cache);
        }

        // Every check passed before anything was written, so the two fields of the window can never
        // disagree with each other.
        foreach (var cache in caches)
            cache.SetValue(null, IsInvalidTone);

        reason = "";
        return true;
    }
}
