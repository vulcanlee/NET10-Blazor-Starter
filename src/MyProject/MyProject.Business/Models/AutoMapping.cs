using AutoMapper;
using MyProject.AccessDatas.Models;
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
        CreateMap<Meeting, MeetingAdapterModel>();
        CreateMap<MeetingAdapterModel, Meeting>();
        CreateMap<Meeting, MeetingDto>()
            .ForMember(dest => dest.ProjectTitle, opt => opt.MapFrom(src => src.Project != null ? src.Project.Title : null));
        CreateMap<MeetingDto, Meeting>();
        CreateMap<Meeting, MeetingCreateUpdateDto>();
        CreateMap<MeetingCreateUpdateDto, Meeting>();
        CreateMap<MeetingFile, MeetingFileAdapterModel>();
        CreateMap<MeetingFileAdapterModel, MeetingFile>();
        #endregion

        #region MyTas
        CreateMap<MyTas, MyTasAdapterModel>();
        CreateMap<MyTasAdapterModel, MyTas>();
        CreateMap<MyTas, MyTaskDto>()
            .ForMember(dest => dest.ProjectTitle, opt => opt.MapFrom(src => src.Project != null ? src.Project.Title : null));
        CreateMap<MyTaskDto, MyTas>();
        CreateMap<MyTas, MyTaskCreateUpdateDto>();
        CreateMap<MyTaskCreateUpdateDto, MyTas>();
        CreateMap<MyTasFile, MyTasFileAdapterModel>();
        CreateMap<MyTasFileAdapterModel, MyTasFile>();
        #endregion

        #region Project
        CreateMap<Project, ProjectAdapterModel>();
        CreateMap<ProjectAdapterModel, Project>();
        CreateMap<Project, ProjectDto>();
        CreateMap<ProjectDto, Project>();
        CreateMap<Project, ProjectCreateUpdateDto>();
        CreateMap<ProjectCreateUpdateDto, Project>();
        CreateMap<ProjectFile, ProjectFileAdapterModel>();
        CreateMap<ProjectFileAdapterModel, ProjectFile>();
        #endregion

        #region RoleView
        CreateMap<RoleView, RoleViewAdapterModel>();
        CreateMap<RoleViewAdapterModel, RoleView>();
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
