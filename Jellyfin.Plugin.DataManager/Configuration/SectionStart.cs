// SPDX-License-Identifier: GPL-3.0-only
namespace Jellyfin.Plugin.DataManager.Configuration;

/// <summary>
/// How a section of the dashboard starts when the page opens.
/// </summary>
public enum SectionStart
{
    /// <summary>The way it was last left in this browser.</summary>
    Remember,

    /// <summary>Always open.</summary>
    Open,

    /// <summary>Always rolled up.</summary>
    RolledUp
}
