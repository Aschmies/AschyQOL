namespace MinimapPlugin.Models;

public sealed class ZoneMapInfo
{
    public uint MapRowId { get; set; }
    public string MapId { get; set; } = string.Empty;
    public string TexturePath { get; set; } = string.Empty;

    /// <summary>Lumina Map.SizeFactor — scale multiplier (divide by 100 to get float scale).</summary>
    public ushort SizeFactor { get; set; }

    /// <summary>Lumina Map.OffsetX — world X offset applied before scale.</summary>
    public short OffsetX { get; set; }

    /// <summary>Lumina Map.OffsetY — world Z offset applied before scale.</summary>
    public short OffsetY { get; set; }
}
