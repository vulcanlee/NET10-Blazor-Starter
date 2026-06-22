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

        #region Meeting
        CreateMap<Meeting, MeetingAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<MeetingAdapterModel, Meeting>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<Meeting, MeetingDto>()
            .ForMember(dest => dest.ProjectTitle, opt => opt.MapFrom(src => src.Project != null ? src.Project.Title : null));
        CreateMap<MeetingDto, Meeting>();
        CreateMap<Meeting, MeetingCreateUpdateDto>();
        CreateMap<MeetingCreateUpdateDto, Meeting>();
        CreateMap<MeetingFile, MeetingFileAdapterModel>();
        CreateMap<MeetingFileAdapterModel, MeetingFile>();
        #endregion

        #region MyTas
        CreateMap<MyTask, MyTasAdapterModel>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToList(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToList(s.Teams)));
        CreateMap<MyTasAdapterModel, MyTask>()
            .ForMember(d => d.Categories, o => o.MapFrom(s => TagStringHelper.ToStored(s.Categories)))
            .ForMember(d => d.Teams, o => o.MapFrom(s => TagStringHelper.ToStored(s.Teams)));
        CreateMap<MyTask, MyTaskDto>()
            .ForMember(dest => dest.ProjectTitle, opt => opt.MapFrom(src => src.Project != null ? src.Project.Title : null));
        CreateMap<MyTaskDto, MyTask>();
        CreateMap<MyTask, MyTaskCreateUpdateDto>();
        CreateMap<MyTaskCreateUpdateDto, MyTask>();
        CreateMap<MyTasFile, MyTasFileAdapterModel>();
        CreateMap<MyTasFileAdapterModel, MyTasFile>();
        #endregion

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

        #region Category
        CreateMap<Category, CategoryAdapterModel>();
        CreateMap<CategoryAdapterModel, Category>();
        CreateMap<Category, CategoryDto>();
        CreateMap<CategoryDto, Category>();
        CreateMap<Category, CategoryCreateUpdateDto>();
        CreateMap<CategoryCreateUpdateDto, Category>();
        #endregion

        #region Team
        CreateMap<Team, TeamAdapterModel>();
        CreateMap<TeamAdapterModel, Team>();
        CreateMap<Team, TeamDto>();
        CreateMap<TeamDto, Team>();
        CreateMap<Team, TeamCreateUpdateDto>();
        CreateMap<TeamCreateUpdateDto, Team>();
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
