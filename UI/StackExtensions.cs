namespace RustedShpizhionStudio.UI;

internal static class StackExtensions
{
    public static void RemoveBottom<T>(this Stack<T> stack)
    {
        if (stack.Count == 0) return;
        var values = stack.ToArray();
        stack.Clear();
        for (var i = values.Length - 2; i >= 0; i--) stack.Push(values[i]);
    }
}
