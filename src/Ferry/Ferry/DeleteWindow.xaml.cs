using System.Windows;

namespace Ferry;

// The one dialog in Ferry where getting it wrong costs the user a file, so it
// says exactly what will happen and nothing else.
//
// The wording is deliberate about where the files live. Ferry only knows about
// its own store, data\files on this laptop; anything the user explicitly saved
// somewhere else is not tracked and cannot be deleted from here. Claiming to
// delete "from your downloads folder" would be a promise Ferry cannot keep.
public sealed partial class DeleteWindow : Window
{
    public bool DeleteFiles => AlsoFiles.IsChecked == true;

    public DeleteWindow(int messageCount, int fileCount)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(this, App.IsDark);

        TitleLine.Text = messageCount == 1
            ? "Delete this message?"
            : $"Delete {messageCount} messages?";
        BodyLine.Text = "Removed from the thread on both devices. No undo.";

        if (fileCount > 0)
        {
            FileOption.Visibility = Visibility.Visible;
            FilesLine.Text = fileCount == 1
                ? "Delete the stored file too"
                : $"Delete {fileCount} stored files too";
            FilesHint.Text = fileCount == 1
                ? "Unchecked moves it to Ferry's kept folder. Saved copies stay untouched."
                : "Unchecked moves them to Ferry's kept folder. Saved copies stay untouched.";
        }

        DeleteButton.Click += (_, _) => DialogResult = true;
        // Focus Cancel, not Delete: IsDefault already gives Delete the Enter
        // key, and a destructive button should not also be the one a stray
        // space bar presses.
        Loaded += (_, _) => CancelButton.Focus();
    }
}
