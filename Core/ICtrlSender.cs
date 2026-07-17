namespace KronosScreenRemote;

// Thin seam so a view-model can send a front-panel/daemon command without depending
// on MainWindow directly. MainWindow implements this by forwarding to its existing
// Ctrl(string) helper (logging + activity-notification side effects included).
interface ICtrlSender
{
    void Send(string cmd);
}
