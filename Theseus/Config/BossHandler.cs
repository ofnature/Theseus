namespace Theseus.Config;

/// <summary>Which plugin handles boss mechanics.</summary>
public enum BossHandler
{
    /// <summary>BossMod Reborn: detection by IPC, AI switched on with <c>/bmrai on</c>.</summary>
    BossModReborn,

    /// <summary>Minerva: detection by IPC, AI switched on by claiming a named dodge preset.</summary>
    Minerva,
}
