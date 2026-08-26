using AutoMapper;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Dtos.Models;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MyProject.Models.Systems;

public class AutoMapping : Profile
{
    public AutoMapping()
    {
        #region Blazor AdapterModel

        #region Project
        CreateMap<Project, ProjectAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<ProjectAdapterModel, Project>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<Project, ProjectDto>();
        CreateMap<ProjectDto, Project>();
        CreateMap<Project, ProjectCreateUpdateDto>();
        CreateMap<ProjectCreateUpdateDto, Project>();
        CreateMap<ProjectFile, ProjectFileAdapterModel>();
        CreateMap<ProjectFileAdapterModel, ProjectFile>();
        #endregion

        #region RoleView
        CreateMap<RoleView, RoleViewAdapterModel>()
            .ForMember(d => d.DefaultTeams, o => o.MapFrom(s => TeamJsonHelper.Deserialize(s.DefaultTeamsJson)));
        CreateMap<RoleViewAdapterModel, RoleView>()
            .ForMember(d => d.DefaultTeamsJson, o => o.MapFrom(s => TeamJsonHelper.Serialize(s.DefaultTeams)));
        #endregion

        // 所有「→ Entity」的映射都以 NameNormalizer 正規化名稱／代號，
        // 讓不論從 Blazor 或 Web API 進來，寫進資料庫的值都已去除前後空白、
        // 空白代號歸一成 null。唯一性檢查才不會出現「比對的與儲存的不是同一個字串」。
        #region Category
        CreateMap<Category, CategoryAdapterModel>()
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<CategoryAdapterModel, Category>()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<Category, CategoryDto>();
        CreateMap<CategoryDto, Category>()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)));
        CreateMap<Category, CategoryCreateUpdateDto>();
        CreateMap<CategoryCreateUpdateDto, Category>()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)));
        #endregion

        #region Team
        CreateMap<Team, TeamAdapterModel>();
        CreateMap<TeamAdapterModel, Team>()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Code, o => o.MapFrom(s => NameNormalizer.NormalizeOptional(s.Code)));
        CreateMap<Team, TeamDto>();
        CreateMap<TeamDto, Team>()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Code, o => o.MapFrom(s => NameNormalizer.NormalizeOptional(s.Code)));
        CreateMap<Team, TeamCreateUpdateDto>();
        CreateMap<TeamCreateUpdateDto, Team>()
            .ForMember(d => d.Name, o => o.MapFrom(s => NameNormalizer.Normalize(s.Name)))
            .ForMember(d => d.Code, o => o.MapFrom(s => NameNormalizer.NormalizeOptional(s.Code)));
        #endregion

        #region MyUser
        CreateMap<MyUser, MyUserAdapterModel>();
        CreateMap<MyUserAdapterModel, MyUser>();
        CreateMap<MyUserAdapterModel, CurrentUser>()
            .ForMember(dest => dest.RoleJson, opt => opt.Ignore())
            .ForMember(dest => dest.RoleList, opt => opt.Ignore())
            .ForMember(dest => dest.IsAuthenticated, opt => opt.Ignore());
        #endregion
        #endregion
    }
}
