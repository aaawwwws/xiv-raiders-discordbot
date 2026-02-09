namespace RaidDcBot.Models;

public enum RecruitIngameFillStatus
{
    Success = 0,
    PostNotFound = 1,
    PositionNotFound = 2,
    PositionOccupied = 3,
    PositionAlreadyClosed = 4,
    PostClosed = 5,
    JobTaken = 6,
    JobRestricted = 7
}
