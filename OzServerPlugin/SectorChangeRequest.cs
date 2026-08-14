using vatsys;

namespace OzServerPlugin;

// A pending request to hand a sector between two controllers, mirroring a row from the
// OzServer backend's GET /sector-requests response. Id is the backend's sector_ownership_requests
// primary key, needed to address the accept/reject/cancel endpoints.
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
