namespace RaidDcBot.Models;

public enum RecruitRequiredJobUpdateStatus : short
{
    Success = 0,
    PostNotFound = 1,
    PostClosed = 2,
    PositionNotFound = 3,
    PositionOccupiedByOtherJob = 4
}
