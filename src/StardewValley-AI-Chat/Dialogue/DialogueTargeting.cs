namespace StardewAI.Dialogue;

public static class DialogueTargeting
{
    public static bool IsInReach(float deltaX, float deltaY, int facingDirection)
    {
        if (deltaX * deltaX + deltaY * deltaY > 4)
            return false;

        (float forward, float side) = facingDirection switch
        {
            0 => (-deltaY, deltaX),
            1 => (deltaX, deltaY),
            2 => (deltaY, deltaX),
            3 => (-deltaX, deltaY),
            _ => (0, 0)
        };
        return forward > 0 && Math.Abs(side) <= forward;
    }
}
