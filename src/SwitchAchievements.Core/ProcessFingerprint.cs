namespace SwitchAchievements.Core;

/// <summary>
/// Uniquely identifies one operating-system process instance. A process id alone
/// is not enough because Windows can reuse ids after a process exits; combining
/// it with the process start time (UTC) yields a stable, unique identity.
/// </summary>
public readonly record struct ProcessFingerprint(int ProcessId, DateTime ProcessStartUtc);