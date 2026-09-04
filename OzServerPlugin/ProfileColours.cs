using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using vatsys;

namespace OzServerPlugin;

// Lets a profile set the plugin's own colours, in the same Colours.xml everything else in vatSys is
// coloured from, so changing them is the ordinary vatSys thing rather than a plugin setting nobody
// would think to look for.
//
// The entries are OzServerColour, not Colour, and that is the whole trick. vatSys loads colours with
// GetElementsByTagName("Colour"), so any other tag name is invisible to it and safe to add:
//
//   <OzServerColour id="Drawing">
//     <R>0</R><G>220</G><B>255</B><A>220</A>
//   </OzServerColour>
//
// It cannot be a normal <Colour> entry. vatSys parses that element's id straight into its
// Colours.Identities enum with Enum.Parse, and it does so outside any try/catch - so a <Colour> with
// an id of our own does not get ignored, it throws out of LoadColours and takes every colour in the
// client with it. (An empty id is skipped instead, but then nothing is registered either.) Reusing a
// real identity would work only by repainting whatever else that identity colours.
//
// Alpha is ours - vatSys's own entries carry only R, G and B, because nothing it draws is
// translucent. It is optional and defaults to opaque.
public static class ProfileColours
{
    public readonly struct Rgba
    {
        public Rgba(byte red, byte green, byte blue, byte alpha)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
        }

        public byte Red { get; }
        public byte Green { get; }
        public byte Blue { get; }
        public byte Alpha { get; }
    }

    // Read once. The file is only read at startup by vatSys too, so re-reading it per lookup would
    // buy nothing but disk access - and this is called while building a map layer.
    static XDocument? _document;
    static bool _loadAttempted;
    static readonly object Lock = new();

    // Returns the profile's value for this id, or fallback when the profile says nothing - which is
    // the normal case, since none of this has to be present for the plugin to work.
    public static Rgba Resolve(string id, Rgba fallback)
    {
        try
        {
            var element = Document()?
                .Descendants("OzServerColour")
                .FirstOrDefault(entry => string.Equals((string?)entry.Attribute("id"), id,
                                                       StringComparison.OrdinalIgnoreCase));

            if (element == null)
                return fallback;

            // Each channel falls back independently, so a partial entry is still usable rather than
            // being discarded whole - someone overriding only the alpha should not have to restate
            // a colour they were happy with.
            return new Rgba(
                Channel(element, "R", fallback.Red),
                Channel(element, "G", fallback.Green),
                Channel(element, "B", fallback.Blue),
                Channel(element, "A", fallback.Alpha));
        }
        catch (Exception ex)
        {
            ActionLog.Log("Colours", $"could not read {id} from Colours.xml: {ex.Message}");
            return fallback;
        }
    }

    static byte Channel(XElement element, string name, byte fallback) =>
        byte.TryParse((string?)element.Element(name), out var value) ? value : fallback;

    static XDocument? Document()
    {
        lock (Lock)
        {
            if (_loadAttempted)
                return _document;

            _loadAttempted = true;

            var path = ColoursPath();
            if (path == null || !File.Exists(path))
                return null;

            _document = XDocument.Load(path);
            return _document;
        }
    }

    // The same file vatSys itself loads: Settings.Default.DatasetPath + "\Colours.xml". Reached by
    // reflection because the generated settings class is internal, and resolved off the vatSys
    // assembly a public type already gives us rather than by assembly name.
    static string? ColoursPath()
    {
        foreach (var candidate in new[] { FromSettings(), FromPluginLocation() })
        {
            if (candidate != null && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    static string? FromSettings()
    {
        try
        {
            var settingsType = typeof(MMI).Assembly.GetType("vatsys.Properties.Settings");

            var settings = settingsType?
                .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);

            var datasetPath = settingsType?
                .GetProperty("DatasetPath", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(settings) as string;

            return string.IsNullOrEmpty(datasetPath) ? null : Path.Combine(datasetPath!, "Colours.xml");
        }
        catch
        {
            return null;
        }
    }

    // Falls back to walking up from this assembly, which sits at
    // <profile>\Plugins\OzServerPlugin\OzServerPlugin.dll - so the profile root, and Colours.xml
    // with it, is two directories above. Independent of any setting, and correct for whichever
    // profile actually loaded this copy of the plugin.
    static string? FromPluginLocation()
    {
        try
        {
            var pluginDirectory = Path.GetDirectoryName(typeof(ProfileColours).Assembly.Location);
            var profileRoot = Path.GetFullPath(Path.Combine(pluginDirectory!, "..", ".."));

            return Path.Combine(profileRoot, "Colours.xml");
        }
        catch
        {
            return null;
        }
    }
}
