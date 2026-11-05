using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ExperimentMonitor {
    static class ControlRunner {
        [STAThread]
        static int Main(string[] args) {
            string suite = args.Length > 1 ? args[1] : "control";
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
            try {
                Console.WriteLine("STEP load " + root);
                var points = Configuration.Load(Path.Combine(root, "protocol.json"));
                Console.WriteLine("STEP suite=" + suite + " points=" + points.Count);
                int count = 0;
                switch (suite) {
                    case "control": {
                        var checks = ControlTests.Run(points, root);
                        File.WriteAllText(Path.Combine(root, "control-test-result.json"),
                            new JavaScriptSerializer().Serialize(new { passed = true, count = checks.Count, checks = checks, hardware_tested = false }));
                        count = checks.Count;
                        break;
                    }
                    case "core": {
                        var checks = CoreTests.Run(points, root);
                        count = checks.Count;
                        break;
                    }
                    case "storage": {
                        int storage = StorageTests.Run();
                        int records = RecordsTests.Run();
                        int failures = ExportFailureTests.Run(points, root);
                        count = storage + records + failures;
                        break;
                    }
                    case "workflow": count = WorkflowTests.Run(points, root); break;
                    case "ui": count = UiLayoutTests.Run(points, root); break;
                    case "smoke": count = Smoke(points, root); break;
                    case "demo": {
                        string isolated = Path.Combine(root, "demo");
                        Directory.CreateDirectory(isolated);
                        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "protocol.json"), Path.Combine(isolated, "protocol.json"), true);
                        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "control-config.json"), Path.Combine(isolated, "control-config.json"), true);
                        using (var form = new DashboardForm(points, isolated)) {
                            form.ShowInTaskbar = false; form.Opacity = 1;
                            form.Show(); Application.DoEvents();
                            form.Engine.Connect(true, "SIM", 9600);
                            form.Engine.Start(1, 1, System.Linq.Enumerable.Select(points, p => p.name));
                            var clock = DateTime.UtcNow;
                            while ((form.Engine.LastObservation("D2202") == null || form.Engine.LastObservation("D2203") == null) && (DateTime.UtcNow - clock).TotalSeconds < 10) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                            form.MainNavigation.SelectedIndex = 1; Application.DoEvents();
                            ControlPage control = Find<ControlPage>(form.MainNavigation.TabPages[1]);
                            var switchButton = Find<Button>(control, b => b.Text.StartsWith("控制开关"));
                            switchButton.PerformClick(); Application.DoEvents();
                            var plan = Find<DataGridView>(control);
                            if (plan.Rows.Count > 0) plan.Rows[0].Cells[3].Value = "26";
                            if (plan.Rows.Count > 1) plan.Rows[1].Cells[3].Value = "2";
                            Application.DoEvents();
                            var send = Find<Button>(control, b => b.Text.StartsWith("发送全部"));
                            send.PerformClick();
                            for (int i = 0; i < 140; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                            string shot = Path.Combine(root, "demo-control.png");
                            CaptureWindow(form, shot);
                            System.Threading.Thread.Sleep(2200);
                            Application.DoEvents();
                            CaptureWindow(form, Path.Combine(root, "demo-control-late.png"));
                            var result = form.Engine.LastControlResult;
                            Console.WriteLine("DEMO status=" + (result == null ? "none" : result.Status) + " old=" + (result == null ? "-" : result.OldValue.ToString()) + " new=" + (result == null ? "-" : result.NewValue.ToString()) + " readback=" + (result != null && result.HasReadback ? result.Readback.Value.ToString() : "-"));
                            Console.WriteLine("DEMO shot=" + shot);
                            form.Size = new System.Drawing.Size(960, 640);
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(400);
                            form.MainNavigation.SelectedIndex = 1;
                            Application.DoEvents();
                            CaptureWindow(form, Path.Combine(root, "demo-control-960.png"));
                            form.Engine.Stop(); form.Engine.Disconnect();
                        }
                        count = 7;
                        break;
                    }
                    case "layout": {
                        string output = Path.Combine(root, "layout");
                        Directory.CreateDirectory(output);
                        var cases = new List<Dictionary<string, object>>();
                        var runSize = typeof(UiLayoutTests).GetMethod("RunSize", BindingFlags.NonPublic | BindingFlags.Static);
                        int[][] sizes = new int[][] { new int[] { 960, 640 }, new int[] { 1440, 900 }, new int[] { 2560, 1600 } };
                        float[] scales = new float[] { 1f, 1.25f, 1.5f };
                        foreach (int[] size in sizes)
                            foreach (float scale in scales) {
                                string label = scale == 1f ? "100%" : (scale == 1.25f ? "125% simulatedDPI" : "150% simulatedDPI");
                                try {
                                    runSize.Invoke(null, new object[] { points, output, size[0], size[1], scale, label, cases });
                                } catch (Exception inner) {
                                    Exception deepest = inner;
                                    while (deepest.InnerException != null) deepest = deepest.InnerException;
                                    Console.WriteLine("LAYOUT FAIL " + size[0] + "x" + size[1] + " " + label + " : " + deepest.Message);
                                }
                                count++;
                            }
                        count += Directory.GetFiles(output, "*.png").Length;
                        Console.WriteLine("LAYOUT cases=" + cases.Count + " screenshots=" + Directory.GetFiles(output, "*.png").Length);
                        break;
                    }
                    case "export": {
                        var payload = new {
                            module = "experiment",
                            schemaVersion = 1,
                            catalogVersion = PointCatalog.Version,
                            points = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(points, p => new {
                                id = p.name, equipmentId = p.binding.device, slave = p.binding.slave,
                                addressZeroBased = p.binding.address_zero_based, registerCount = p.binding.register_count,
                                metadata = PointCatalog.Get(p)
                            }))
                        };
                        File.WriteAllText(Path.Combine(root, "point-contract.json"), new JavaScriptSerializer().Serialize(payload));
                        count = payload.points.Length;
                        break;
                    }
                    default: throw new ArgumentException("unknown suite: " + suite);
                }
                Console.WriteLine("CHECKS=" + count);
                return 0;
            } catch (Exception ex) {
                Exception deepest = ex;
                while (deepest.InnerException != null) deepest = deepest.InnerException;
                string text = ex.GetType().FullName + Environment.NewLine + ex.Message + Environment.NewLine + "INNER: " + deepest.Message + Environment.NewLine + ex.StackTrace;
                try { File.WriteAllText(Path.Combine(root, "runner-error.txt"), text); } catch { }
                Console.WriteLine("RUNNER FAIL " + ex.GetType().FullName + " " + ex.Message);
                Console.WriteLine(text);
                return 2;
            }
        }

        static int Smoke(List<Point> points, string root) {
            int checks = 0;
            string output = Path.Combine(root, "smoke");
            Directory.CreateDirectory(output);
            string isolated = Path.Combine(output, "form");
            Directory.CreateDirectory(isolated);
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "protocol.json"), Path.Combine(isolated, "protocol.json"), true);
            using (var form = new DashboardForm(points, isolated)) {
                TabControl nav = form.MainNavigation;
                Check(nav.TabPages.Count == 6, "six navigation pages");
                Check(nav.TabPages[0].Text == "实时监控", "live page first");
                Check(nav.TabPages[1].Text == "运行控制", "control page second");
                Check(nav.TabPages[2].Text == "趋势分析", "trends third");
                Check(nav.TabPages[3].Text == "数据记录", "records fourth");
                Check(nav.TabPages[4].Text == "云端连接", "cloud fifth");
                Check(nav.TabPages[5].Text == "通信诊断", "diagnostics sixth");
                ControlPage control = Find<ControlPage>(nav.TabPages[1]);
                Check(control != null, "control page instance attached");
                Check(Find<Button>(control, b => b.Text.StartsWith("控制开关")) != null, "switch button exists");
                Check(Find<Button>(control, b => b.Text.StartsWith("发送全部")) != null, "batch send button exists");
                Check(Find<Button>(control, b => b.Text.Contains("导出日志")) != null, "export button exists");
                Check(Find<ListBox>(control)!=null||Find<RichTextBox>(control)!=null, "process log exists");
                Check(Find<DataGridView>(control) != null, "plan grid exists");
                checks += 14;
            }

            var records = typeof(PageWorkflowTests).GetMethod("Records", BindingFlags.NonPublic | BindingFlags.Static);
            Check(records != null, "records workflow method exists");
            checks++;
            checks += (int)records.Invoke(null, new object[] { points, output });
            var diagnostic = typeof(PageWorkflowTests).GetMethod("Diagnostic", BindingFlags.NonPublic | BindingFlags.Static);
            checks += (int)diagnostic.Invoke(null, new object[] { points, output, false });

            var runSize = typeof(UiLayoutTests).GetMethod("RunSize", BindingFlags.NonPublic | BindingFlags.Static);
            Check(runSize != null, "layout size method exists");
            checks++;
            var cases = new List<Dictionary<string, object>>();
            runSize.Invoke(null, new object[] { points, output, 960, 640, 1f, "100%", cases });
            Check(cases.Count == 1, "one layout case completed");
            checks++;
            Console.WriteLine("SMOKE checks=" + checks);
            return checks;
        }

        static void Check(bool ok, string name) {
            if (!ok) throw new Exception("冒烟检查失败：" + name);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

        /// <summary>RichTextBox 不响应 DrawToBitmap，这里用 PrintWindow 抓真实窗口内容。</summary>
        static void CaptureWindow(Form form, string path) {
            using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height)) {
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) {
                    IntPtr hdc = graphics.GetHdc();
                    try { PrintWindow(form.Handle, hdc, 2); } finally { graphics.ReleaseHdc(hdc); }
                }
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        static T Find<T>(Control root) where T : Control {
            foreach (Control child in root.Controls) {
                T value = child as T;
                if (value != null) return value;
                T nested = Find<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        static T Find<T>(Control root, Func<T, bool> match) where T : Control {
            foreach (Control child in root.Controls) {
                T value = child as T;
                if (value != null && (match == null || match(value))) return value;
                T nested = Find<T>(child, match);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
