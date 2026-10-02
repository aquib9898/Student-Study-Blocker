namespace StudyBlocker.Service;

public interface IStateStore
{
    T? Read<T>(string fileName) where T : class;
    void Write<T>(string fileName, T value);
}
