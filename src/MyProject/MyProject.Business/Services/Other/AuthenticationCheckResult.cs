namespace MyProject.Business.Services.Other;

public enum AuthenticationCheckResult
{
    Succeeded,
    Unauthenticated,
    InvalidUser,
    RequiresPasswordChange,
    /// <summary>工作階段已失效（0.9.103 起）：改密碼、停用、角色變更、強制登出後的舊登入。</summary>
    SessionRevoked
}
