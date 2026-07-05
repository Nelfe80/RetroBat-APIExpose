namespace RetroBat.Domain.Interfaces;

public interface IRuleCompiler
{
    object Compile(string source);
}

public interface IRuleEngine
{
    Task ApplyAsync(object trigger, object context);
}
