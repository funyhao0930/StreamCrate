namespace StreamCrate.App.Presentation;

internal static class PageEntranceAnimationScheduler
{
    public static bool RequiresLayoutPass(int itemCount, int realizedContainerCount) =>
        itemCount > realizedContainerCount;
}
