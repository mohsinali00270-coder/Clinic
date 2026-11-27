using Clinic.Shared.Contracts;
using Clinic.Shared.Forms;
using Clinic.Shared.Scheduling;

namespace Clinic.App.Services;

/// <summary>Typed calls for the admin screens (branches, users, roles, devices).</summary>
public sealed class AdminApi(ApiClient api)
{
    public Task<List<BranchDto>> GetBranchesAsync() => api.GetAsync<List<BranchDto>>(ApiRoutes.Branches);
    public Task<BranchDto> CreateBranchAsync(SaveBranchRequest r) => api.PostAsync<BranchDto>(ApiRoutes.Branches, r);
    public Task<BranchDto> UpdateBranchAsync(Guid id, SaveBranchRequest r) => api.PutAsync<BranchDto>($"{ApiRoutes.Branches}/{id}", r);

    public Task<List<RoleDto>> GetRolesAsync() => api.GetAsync<List<RoleDto>>(ApiRoutes.Roles);

    public Task<PagedResult<UserDto>> GetUsersAsync(string? search, int page = 1) =>
        api.GetAsync<PagedResult<UserDto>>($"{ApiRoutes.Users}?page={page}&pageSize=100&search={Uri.EscapeDataString(search ?? "")}");
    public Task<UserDto> GetUserAsync(Guid id) => api.GetAsync<UserDto>($"{ApiRoutes.Users}/{id}");
    public Task<UserDto> CreateUserAsync(CreateUserRequest r) => api.PostAsync<UserDto>(ApiRoutes.Users, r);
    public Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest r) => api.PutAsync<UserDto>($"{ApiRoutes.Users}/{id}", r);
    public Task ResetPasswordAsync(Guid id, string password) =>
        api.PostAsync($"{ApiRoutes.Users}/{id}/reset-password", new ResetPasswordRequest(password));

    public Task<List<DeviceDto>> GetDevicesAsync() => api.GetAsync<List<DeviceDto>>(ApiRoutes.Devices);
    public Task<DeviceDto> RevokeDeviceAsync(Guid id) => api.PostAsync<DeviceDto>($"{ApiRoutes.Devices}/{id}/revoke", null);
    public Task<DeviceDto> RenameDeviceAsync(Guid id, string name) =>
        api.PutAsync<DeviceDto>($"{ApiRoutes.Devices}/{id}/name", new RenameDeviceRequest(name));

    // ---- Dynamic forms (online admin tool; devices get published versions through sync)

    public Task<List<EntityTypeDto>> GetEntityTypesAsync() => api.GetAsync<List<EntityTypeDto>>(FormRoutes.EntityTypes);
    public Task<List<FormSummaryDto>> GetFormsAsync() => api.GetAsync<List<FormSummaryDto>>(FormRoutes.Forms);
    public Task<FormDto> GetFormAsync(Guid id) => api.GetAsync<FormDto>($"{FormRoutes.Forms}/{id}");
    public Task<FormDto> CreateFormAsync(CreateFormRequest r) => api.PostAsync<FormDto>(FormRoutes.Forms, r);
    public Task<FormDto> SaveFormAsync(Guid id, SaveFormDraftRequest r) => api.PutAsync<FormDto>($"{FormRoutes.Forms}/{id}", r);
    public Task<FormVersionDto> PublishFormAsync(Guid id) => api.PostAsync<FormVersionDto>($"{FormRoutes.Forms}/{id}/publish", null);
    public Task<List<FormVersionDto>> GetFormVersionsAsync(Guid id) => api.GetAsync<List<FormVersionDto>>($"{FormRoutes.Forms}/{id}/versions");
    public Task<FormDto> CloneFormAsync(Guid id) => api.PostAsync<FormDto>($"{FormRoutes.Forms}/{id}/clone", null);
    public Task<FormDto> ImportFormAsync(FormDefinition d) => api.PostAsync<FormDto>($"{FormRoutes.Forms}/import", d);
    public Task<List<FormTemplateDto>> GetTemplatesAsync(string entityType) =>
        api.GetAsync<List<FormTemplateDto>>($"{FormRoutes.FormTemplates}?entityType={Uri.EscapeDataString(entityType)}");

    public Task<List<LookupListDto>> GetLookupListsAsync() => api.GetAsync<List<LookupListDto>>(FormRoutes.Lookups);
    public Task<LookupListDetailDto> GetLookupListAsync(Guid id) => api.GetAsync<LookupListDetailDto>($"{FormRoutes.Lookups}/{id}");
    public Task<LookupListDto> CreateLookupListAsync(SaveLookupListRequest r) => api.PostAsync<LookupListDto>(FormRoutes.Lookups, r);
    public Task<LookupItemDto> SaveLookupItemAsync(Guid listId, Guid? itemId, SaveLookupItemRequest r) => itemId is { } id
        ? api.PutAsync<LookupItemDto>($"{FormRoutes.Lookups}/{listId}/items/{id}", r)
        : api.PostAsync<LookupItemDto>($"{FormRoutes.Lookups}/{listId}/items", r);
    // ---- Doctors and scheduling setup

    public Task<List<DoctorDto>> GetDoctorsAsync() => api.GetAsync<List<DoctorDto>>(SchedulingRoutes.Doctors);
    public Task<DoctorDto> SaveDoctorAsync(Guid? id, SaveDoctorRequest r) => id is { } d
        ? api.PutAsync<DoctorDto>($"{SchedulingRoutes.Doctors}/{d}", r)
        : api.PostAsync<DoctorDto>(SchedulingRoutes.Doctors, r);
    public Task<List<ScheduleDto>> GetSchedulesAsync(Guid doctorId) => api.GetAsync<List<ScheduleDto>>($"{SchedulingRoutes.Doctors}/{doctorId}/schedules");
    public Task<ScheduleDto> AddScheduleAsync(Guid doctorId, SaveScheduleRequest r) => api.PostAsync<ScheduleDto>($"{SchedulingRoutes.Doctors}/{doctorId}/schedules", r);
    public Task DeleteScheduleAsync(Guid doctorId, Guid id) => api.DeleteAsync($"{SchedulingRoutes.Doctors}/{doctorId}/schedules/{id}");
    public Task<List<LeaveDto>> GetLeavesAsync(Guid doctorId) => api.GetAsync<List<LeaveDto>>($"{SchedulingRoutes.Doctors}/{doctorId}/leaves");
    public Task<LeaveDto> AddLeaveAsync(Guid doctorId, SaveLeaveRequest r) => api.PostAsync<LeaveDto>($"{SchedulingRoutes.Doctors}/{doctorId}/leaves", r);
    public Task DeleteLeaveAsync(Guid doctorId, Guid id) => api.DeleteAsync($"{SchedulingRoutes.Doctors}/{doctorId}/leaves/{id}");
    public Task<List<AppointmentTypeDto>> GetAppointmentTypesAsync() => api.GetAsync<List<AppointmentTypeDto>>(SchedulingRoutes.AppointmentTypes);
    public Task<AppointmentTypeDto> SaveAppointmentTypeAsync(Guid? id, SaveAppointmentTypeRequest r) => id is { } t
        ? api.PutAsync<AppointmentTypeDto>($"{SchedulingRoutes.AppointmentTypes}/{t}", r)
        : api.PostAsync<AppointmentTypeDto>(SchedulingRoutes.AppointmentTypes, r);
    public Task<List<BranchClosureDto>> GetClosuresAsync() => api.GetAsync<List<BranchClosureDto>>(SchedulingRoutes.BranchClosures);
    public Task<BranchClosureDto> AddClosureAsync(SaveBranchClosureRequest r) => api.PostAsync<BranchClosureDto>(SchedulingRoutes.BranchClosures, r);
    public Task DeleteClosureAsync(Guid id) => api.DeleteAsync($"{SchedulingRoutes.BranchClosures}/{id}");

    public Task<ImportLookupItemsResult> ImportLookupItemsAsync(Guid listId, string csv) =>
        api.PostAsync<ImportLookupItemsResult>($"{FormRoutes.Lookups}/{listId}/import", new ImportLookupItemsRequest(csv));

    // Phase 8: duplicate merge and audit (online admin screens)
    public Task<List<DuplicatePairDto>> GetDuplicatesAsync() => api.GetAsync<List<DuplicatePairDto>>(HardeningRoutes.Duplicates);
    public Task<MergeResultDto> MergePatientsAsync(MergePatientsRequest r) => api.PostAsync<MergeResultDto>(HardeningRoutes.Merge, r);

    public Task<PagedResult<AuditEntryDto>> GetAuditAsync(string? entityType, string? entityId, string? action, DateTime from, DateTime to, int page)
    {
        static string E(string? v) => Uri.EscapeDataString(v?.Trim() ?? "");
        return api.GetAsync<PagedResult<AuditEntryDto>>(
            $"{HardeningRoutes.Audit}?entityType={E(entityType)}&entityId={E(entityId)}&action={E(action)}&from={from:O}&to={to:O}&page={page}&pageSize=50");
    }
}
