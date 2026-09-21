namespace OpenNanaimo.Adapter.Models;

public enum AccountAuthenticationStatus
{
    Failed = 0,
    Authenticated = 1,
    Registered = 2,
    Banned = 3,
    IpBanned = 4,
    DoubleBanned = 5,
    AlreadyOnline = 6,
    DailyRegistrationLimitReached = 7,
    IpRegistrationLimitReached = 8,
    IpDailyRegistrationLimitReached = 9,
    AuthorizationRequired = 10,
    AccountTrialExpired = 11,
    GlobalTrialExpired = 12,
    AccountTrialNotAuthorized = 13
}

public sealed record AccountAuthenticationResult(
    AccountAuthenticationStatus Status,
    long AccountId,
    string Username,
    string Error)
{
    public bool Success => Status is
        AccountAuthenticationStatus.Authenticated or
        AccountAuthenticationStatus.Registered;
}

public sealed record AccountLoginPolicy(
    bool DailyRegistrationLimitEnabled,
    int DailyRegistrationLimit,
    bool IpRegistrationLimitEnabled,
    int IpRegistrationLimit,
    bool IpDailyRegistrationLimitEnabled,
    int IpDailyRegistrationLimit,
    bool LoginAuthorizationRequired,
    bool PerAccountTrialLimitEnabled,
    bool GlobalTrialLimitEnabled,
    int GlobalTrialMinutes)
{
    public static AccountLoginPolicy Disabled { get; } = new(
        false, 0, false, 0, false, 0, false, false, false, 0);

    public static AccountLoginPolicy From(AdapterOptions options) => new(
        options.DailyRegistrationLimitEnabled,
        options.DailyRegistrationLimit,
        options.IpRegistrationLimitEnabled,
        options.IpRegistrationLimit,
        options.IpDailyRegistrationLimitEnabled,
        options.IpDailyRegistrationLimit,
        options.LoginAuthorizationRequired,
        options.PerAccountTrialLimitEnabled,
        options.GlobalTrialLimitEnabled,
        options.GlobalTrialMinutes);
}

public sealed record AccountTrialAccess(
    bool Limited,
    AccountAuthenticationStatus ExpiredStatus,
    long RemainingSeconds);
