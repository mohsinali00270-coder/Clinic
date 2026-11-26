using Clinic.Api.Security;
using Clinic.Application.Clinical;
using Clinic.Shared.Scheduling;
using Clinic.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Clinic.Api.Controllers;

/// <summary>FR-DOC-01/08, FR-APT-01: doctors, schedules and leave. Devices receive them through sync.</summary>
[ApiController]
[Route(SchedulingRoutes.Doctors)]
public sealed class DoctorsController(DoctorService doctors) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<DoctorDto>> List(CancellationToken ct) => doctors.ListAsync(ct);

    [HttpGet("{id:guid}"), Authorize]
    public Task<DoctorDto> Get(Guid id, CancellationToken ct) => doctors.GetAsync(id, ct);

    [HttpPost, HasPermission(Permissions.Doctors.Manage)]
    public Task<DoctorDto> Create(SaveDoctorRequest r, CancellationToken ct) => doctors.SaveAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.Doctors.Manage)]
    public Task<DoctorDto> Update(Guid id, SaveDoctorRequest r, CancellationToken ct) => doctors.SaveAsync(id, r, ct);

    [HttpGet("{id:guid}/schedules"), Authorize]
    public Task<IReadOnlyList<ScheduleDto>> Schedules(Guid id, CancellationToken ct) => doctors.SchedulesAsync(id, ct);

    [HttpPost("{id:guid}/schedules"), HasPermission(Permissions.Doctors.Manage)]
    public Task<ScheduleDto> AddSchedule(Guid id, SaveScheduleRequest r, CancellationToken ct) => doctors.SaveScheduleAsync(id, null, r, ct);

    [HttpPut("{id:guid}/schedules/{scheduleId:guid}"), HasPermission(Permissions.Doctors.Manage)]
    public Task<ScheduleDto> UpdateSchedule(Guid id, Guid scheduleId, SaveScheduleRequest r, CancellationToken ct) =>
        doctors.SaveScheduleAsync(id, scheduleId, r, ct);

    [HttpDelete("{id:guid}/schedules/{scheduleId:guid}"), HasPermission(Permissions.Doctors.Manage)]
    public async Task<IActionResult> DeleteSchedule(Guid id, Guid scheduleId, CancellationToken ct)
    {
        await doctors.DeleteScheduleAsync(id, scheduleId, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/leaves"), Authorize]
    public Task<IReadOnlyList<LeaveDto>> Leaves(Guid id, CancellationToken ct) => doctors.LeavesAsync(id, ct);

    [HttpPost("{id:guid}/leaves"), HasPermission(Permissions.Doctors.Manage)]
    public Task<LeaveDto> AddLeave(Guid id, SaveLeaveRequest r, CancellationToken ct) => doctors.AddLeaveAsync(id, r, ct);

    [HttpDelete("{id:guid}/leaves/{leaveId:guid}"), HasPermission(Permissions.Doctors.Manage)]
    public async Task<IActionResult> DeleteLeave(Guid id, Guid leaveId, CancellationToken ct)
    {
        await doctors.DeleteLeaveAsync(id, leaveId, ct);
        return NoContent();
    }
}

[ApiController]
[Route(SchedulingRoutes.AppointmentTypes)]
public sealed class AppointmentTypesController(SchedulingSetupService setup) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<AppointmentTypeDto>> List(CancellationToken ct) => setup.TypesAsync(ct);

    [HttpPost, HasPermission(Permissions.Scheduling.Manage)]
    public Task<AppointmentTypeDto> Create(SaveAppointmentTypeRequest r, CancellationToken ct) => setup.SaveTypeAsync(null, r, ct);

    [HttpPut("{id:guid}"), HasPermission(Permissions.Scheduling.Manage)]
    public Task<AppointmentTypeDto> Update(Guid id, SaveAppointmentTypeRequest r, CancellationToken ct) => setup.SaveTypeAsync(id, r, ct);
}

[ApiController]
[Route(SchedulingRoutes.BranchClosures)]
public sealed class BranchClosuresController(SchedulingSetupService setup) : ControllerBase
{
    [HttpGet, Authorize]
    public Task<IReadOnlyList<BranchClosureDto>> List(CancellationToken ct) => setup.ClosuresAsync(ct);

    [HttpPost, HasPermission(Permissions.Scheduling.Manage)]
    public Task<BranchClosureDto> Create(SaveBranchClosureRequest r, CancellationToken ct) => setup.AddClosureAsync(r, ct);

    [HttpDelete("{id:guid}"), HasPermission(Permissions.Scheduling.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await setup.DeleteClosureAsync(id, ct);
        return NoContent();
    }
}

/// <summary>FR-OFF-23: chunked, resumable upload and download of attachment files.</summary>
[ApiController]
[Authorize]
[Route(SchedulingRoutes.Files)]
public sealed class FilesController(FileService files) : ControllerBase
{
    [HttpGet("{id:guid}/status")]
    public Task<UploadStatusDto> Status(Guid id, CancellationToken ct) => files.StatusAsync(id, ct);

    [HttpPut("{id:guid}/chunks/{index:int}")]
    [RequestSizeLimit(FileService.ChunkSize + 1024)]
    public async Task<IActionResult> Chunk(Guid id, int index, CancellationToken ct)
    {
        await files.PutChunkAsync(id, index, Request.Body, Request.ContentLength, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/complete")]
    public Task<UploadStatusDto> Complete(Guid id, CompleteUploadRequest r, CancellationToken ct) => files.CompleteAsync(id, r.TotalChunks, ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var (content, type, name) = await files.OpenAsync(id, ct);
        return File(content, type, name);
    }
}
