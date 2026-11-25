using vatsys;

namespace OzServerPlugin;

// A pending request to hand a sector between two controllers, as the Sectors window shows it. Id is
// the backend's request id, which the accept, reject and cancel endpoints are addressed by;
// Controller is the other party's callsign.
public class SectorChangeRequest
{
    public int Id { get; }
    public SectorsVolumes.Sector Sector { get; }
    public string Controller { get; }

    public SectorChangeRequest(int id, SectorsVolumes.Sector sector, string controller)
    {
        Id = id;
        Sector = sector;
        Controller = controller;
    }
}
