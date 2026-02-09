namespace RaidDcBot.Models;

public enum RecruitAssignStatus
{
    Success = 0,
    PostNotFound = 1,
    PositionNotFound = 2,
    PositionTaken = 3,
    JobTaken = 4,
    PostClosed = 5,
    PositionClosed = 6,
    JobRestricted = 7
}
