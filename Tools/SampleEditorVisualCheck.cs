namespace KronosScreenRemote;

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KronosScreenRemote.ViewModels;

// One-off, throwaway visual verification tool (see App.xaml.cs's
// `--sample-editor-visual-check <path.ksc>` flag) - NOT part of the shipped feature
// set, NOT a permanent regression test (SampleEditorSmokeTest already covers the real
// logic headlessly). This exists purely to answer "does the window actually look
// right" the same way Tools/sample_editor/_gui_smoke_test.py's PrintWindow-based
// screenshots did for the Python POC in an earlier session: drive the REAL window
// (Show()'d, really rendered) through a few real selections and capture what it
// actually looks like, since --ui-theme-smoketest only proves construction doesn't
// throw, not that the layout is usable.
static class SampleEditorVisualCheck
{
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiObj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static void Screenshot(Window w, string name, string outDir)
    {
        w.UpdateLayout();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        hwnd = GetAncestor(hwnd, 2 /* GA_ROOT */);
        GetWindowRect(hwnd, out var rect);
        int width = rect.R - rect.L, height = rect.B - rect.T;
        if (width <= 0 || height <= 0) { Console.WriteLine($"[visual-check] {name}: zero-size window, skipped"); return; }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bmp = CreateCompatibleBitmap(screenDc, width, height);
        var old = SelectObject(memDc, bmp);
        PrintWindow(hwnd, memDc, 2 /* PW_RENDERFULLCONTENT */);

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            CaptureBits(memDc, bmp, width, height), width * 4);

        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, name + ".png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var fs = File.Create(path)) encoder.Save(fs);

        SelectObject(memDc, old);
        DeleteObject(bmp);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        Console.WriteLine($"[visual-check] wrote {path}");
    }

    static byte[] CaptureBits(IntPtr memDc, IntPtr bmp, int width, int height)
    {
        var gdiBmp = System.Drawing.Image.FromHbitmap(bmp);
        using var ms = new MemoryStream();
        var data = new byte[width * height * 4];
        var locked = gdiBmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, data, 0, data.Length);
        gdiBmp.UnlockBits(locked);
        gdiBmp.Dispose();
        return data;
    }

    static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        parent.UpdateLayout();
        return parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    public static void Schedule(string kscPath)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "kronos_sample_editor_visual_check");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);

        // This tool now exercises real button clicks (Add Zone below), which - unlike
        // the plain tree-selection steps that came before it - actually write to disk.
        // Copy the fixture into scratch first, same "never mutate the source" discipline
        // Tools/SampleEditorSmokeTest.cs already applies, so pointing this at a real
        // fixture can't leave it modified.
        var scratchRoot = Path.Combine(Path.GetTempPath(), "kronos_sample_editor_visual_check_scratch");
        if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, recursive: true);
        Directory.CreateDirectory(scratchRoot);
        var kscName = Path.GetFileName(kscPath);
        var contentDirName = Path.GetFileNameWithoutExtension(kscPath);
        var srcContentDir = Path.Combine(Path.GetDirectoryName(kscPath) ?? "", contentDirName);
        var scratchKscPath = Path.Combine(scratchRoot, kscName);
        File.Copy(kscPath, scratchKscPath);
        if (Directory.Exists(srcContentDir)) CopyDirectory(srcContentDir, Path.Combine(scratchRoot, contentDirName));
        kscPath = scratchKscPath;

        // OpenCollectionPath below writes Recent Files to the REAL settings.json
        // (Storage.SaveSettings has no test-injectable override) - snapshot it now and
        // restore it right before Environment.Exit, since Environment.Exit does NOT run
        // pending try/finally blocks, so a finally-based guard here would never fire.
        var settingsPath = Path.Combine(Storage.DataDir, "settings.json");
        var settingsBackup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        void RestoreSettings()
        {
            if (settingsBackup != null) File.WriteAllBytes(settingsPath, settingsBackup);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }

        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            var win = new SampleEditorWindow();
            win.Show();
            await Task.Delay(300);
            Screenshot(win, "01_empty", outDir);

            win.OpenCollectionPath(kscPath);
            await Task.Delay(200);
            win.SampleTree.UpdateLayout();

            void ExpandAll(ItemsControl parent)
            {
                parent.UpdateLayout();
                foreach (var item in parent.Items)
                {
                    if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem child) continue;
                    child.IsExpanded = true;
                    child.UpdateLayout();
                    ExpandAll(child);
                }
            }
            ExpandAll(win.SampleTree);
            await Task.Delay(200);
            Screenshot(win, "02_collection_loaded_expanded", outDir);

            var roots = (System.Collections.ObjectModel.ObservableCollection<SampleTreeNode>)win.SampleTree.ItemsSource;
            if (roots.Count > 0)
            {
                var collectionNode = roots[0];
                if (FindContainer(win.SampleTree, collectionNode) is { } collectionItem)
                {
                    collectionItem.IsSelected = true;
                    await Task.Delay(150);
                    Screenshot(win, "03_collection_node_selected", outDir);
                }

                if (collectionNode.Children.Count > 0)
                {
                    var msNode = collectionNode.Children[0];
                    var msContainer = FindContainer(win.SampleTree, collectionNode) is { } ci
                        ? ci.ItemContainerGenerator.ContainerFromItem(msNode) as TreeViewItem : null;
                    if (msContainer != null)
                    {
                        msContainer.IsSelected = true;
                        await Task.Delay(150);
                        Screenshot(win, "04_multisample_node_selected", outDir);

                        SampleTreeNode? realZone = null, skippedZone = null;
                        foreach (var z in msNode.Children)
                        {
                            if (z.ZoneRef?.Zone.IsSkipped == true) skippedZone ??= z;
                            else realZone ??= z;
                        }

                        if (realZone != null && msContainer.ItemContainerGenerator.ContainerFromItem(realZone) is TreeViewItem realItem)
                        {
                            realItem.IsSelected = true;
                            await Task.Delay(150);
                            Screenshot(win, "05_real_zone_selected", outDir);

                            // The redesigned detail panel (dual stereo waveform panes,
                            // piano keymap, VU meter, Edit/Fade/TempoPitch sections) is
                            // now taller than the window - scroll to the bottom to
                            // verify everything below the fold actually renders too,
                            // not just what fits in the initial unscrolled view.
                            win.DetailScrollViewer.ScrollToEnd();
                            win.DetailScrollViewer.UpdateLayout();
                            await Task.Delay(150);
                            Screenshot(win, "05b_real_zone_selected_scrolled", outDir);
                            win.DetailScrollViewer.ScrollToTop();
                            win.DetailScrollViewer.UpdateLayout();

                            // Tab framework (Keymap/Samples/Looping) - confirms each tab
                            // actually renders its relocated content, not just that the
                            // TabControl itself constructs.
                            //
                            // EVERY tab shot scrolls to the end first. The waveform and
                            // transport were hoisted above the TabControl, so the tabs now
                            // sit below the fold at this window size - screenshotting them
                            // from the top of the scroll produced three IDENTICAL images
                            // of the waveform with no tab content in frame at all, which
                            // silently attested to nothing. (A same-size pair of output
                            // PNGs was the tell.)
                            if (win.EditorTabs.Items.Count >= 3)
                            {
                                async Task ShotTab(int index, string name)
                                {
                                    ((TabItem)win.EditorTabs.Items[index]).IsSelected = true;
                                    await Task.Delay(150);
                                    win.DetailScrollViewer.ScrollToEnd();
                                    win.DetailScrollViewer.UpdateLayout();
                                    await Task.Delay(150);
                                    Screenshot(win, name, outDir);
                                }

                                await ShotTab(0, "05c_keymap_tab");
                                await ShotTab(1, "05e_samples_tab_scrolled");

                                // The one deliberately-unscrolled shot: the hoisted
                                // waveform/transport block at the top of the pane.
                                win.DetailScrollViewer.ScrollToTop();
                                win.DetailScrollViewer.UpdateLayout();
                                await Task.Delay(150);
                                Screenshot(win, "05f_samples_tab_top", outDir);

                                await ShotTab(2, "05d_looping_tab");

                                ((TabItem)win.EditorTabs.Items[0]).IsSelected = true; // back to Keymap
                                await Task.Delay(150);
                            }

                            // Add Zone (items 4/5) - a real button click through the
                            // production Click handler, not a direct ViewModel call, so
                            // this actually exercises the window's own tree-reselection
                            // glue: the newly added zone should end up selected (visible
                            // in the tree AND the keymap), not the parent multisample
                            // node the click used to visually "snap" back to.
                            win.BtnAddZone.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            await Task.Delay(250);
                            Console.WriteLine("[visual-check] after Add Zone, selected tree item: "
                                + ((win.SampleTree.SelectedItem as SampleTreeNode)?.Label ?? "(none)"));
                            ((TabItem)win.EditorTabs.Items[0]).IsSelected = true; // Keymap - show the new zone's bar/highlight
                            await Task.Delay(150);
                            Screenshot(win, "05g_after_add_zone", outDir);
                        }

                        if (skippedZone != null && msContainer.ItemContainerGenerator.ContainerFromItem(skippedZone) is TreeViewItem skipItem)
                        {
                            skipItem.IsSelected = true;
                            await Task.Delay(150);
                            Screenshot(win, "06_skipped_zone_selected", outDir);
                        }
                    }
                }
            }

            Console.WriteLine($"[visual-check] done, screenshots in {outDir}");
            RestoreSettings();
            Environment.Exit(0);
        }, DispatcherPriority.ApplicationIdle);
    }
}
