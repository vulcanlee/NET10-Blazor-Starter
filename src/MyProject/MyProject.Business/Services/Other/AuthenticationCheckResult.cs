namespace MyProject.Business.Services.Other;

public enum AuthenticationCheckResult
{
    Succeeded,
    Unauthenticated,
    InvalidUser,
    RequiresPasswordChange
}
