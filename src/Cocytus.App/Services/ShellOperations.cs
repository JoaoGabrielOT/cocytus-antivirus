using Microsoft.VisualBasic.FileIO;

namespace Cocytus.App.Services;

/// <summary>Operações de arquivo do Windows (as mesmas do Explorador), numa thread STA própria.</summary>
public static class ShellOperations
{
    /// <summary>
    /// Manda a pasta para a Lixeira. Se a pasta exigir administrador (Program Files, ProgramData), o próprio Windows
    /// mostra o aviso para continuar. Lança OperationCanceledException se o usuário cancelar.
    /// </summary>
    public static Task SendFolderToRecycleBinAsync(string folder)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                FileSystem.DeleteDirectory(folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Cocytus.Lixeira",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
