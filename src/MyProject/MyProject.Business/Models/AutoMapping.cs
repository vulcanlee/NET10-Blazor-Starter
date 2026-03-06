using AutoMapper;
using MyProject.AccessDatas.Models;
using MyProject.Models.AdapterModel;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MyProject.Models.Systems;

public class AutoMapping : Profile
{
    public AutoMapping()
    {
        #region Blazor AdapterModel

        #region RoleView
        CreateMap<RoleView, RoleViewAdapterModel>();
        CreateMap<RoleViewAdapterModel, RoleView>();
        #endregion

        #region MyUser
        CreateMap<MyUser, MyUserAdapterModel>();
        CreateMap<MyUserAdapterModel, MyUser>();
        #endregion
        #endregion
    }
}
