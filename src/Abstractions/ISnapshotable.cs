namespace VolcanoBlue.EventSourcing.Abstractions
{
    public interface ISnapshotable<out T>
    {
        bool ShouldTakeSnapshot();

        T TakeSnapshot();
    }
}
