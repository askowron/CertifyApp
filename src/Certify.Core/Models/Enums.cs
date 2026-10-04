namespace Certify.Core.Models;

public enum CertificateAuthority
{
    LetsEncrypt,
    LetsEncryptStaging,
    ZeroSsl,
    Google,
    GoogleStaging,
    CustomAcme
}

public enum ChallengeType
{
    Http01,
    Dns01
}

public enum DeploymentTargetType
{
    IIS,
    Apache,
    Nginx
}

public enum CertificateStatus
{
    NotStarted,
    PendingValidation,
    Valid,
    Expired,
    Error,
    Revoked
}

public enum RenewalMode
{
    Auto,
    Manual
}
