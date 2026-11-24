Imports System.IO
Imports System.Windows.Threading

''' <summary>
''' 主窗口。数据全部由 <see cref="MainViewModel"/> 提供，这里只做装配。
''' 附带两个调试入口：
'''   --shot &lt;png&gt;         把内容渲染成 PNG 后退出（UI 回归对比）
'''   --eval &lt;enc&gt; &lt;cont&gt;   把求值结果打到控制台（接入 UI 之前验证规则）
''' </summary>
Class MainWindow

    Private _vm As MainViewModel

    Private Sub MainWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Dim argv = Environment.GetCommandLineArgs()

        ' --hwcheck <file>：同步跑一遍硬件探测并写报告，然后退出（自动化验证用）
        Dim hwIdx = Array.IndexOf(argv, "--hwcheck")
        If hwIdx >= 0 AndAlso hwIdx + 1 < argv.Length Then
            Dim caps = Capabilities.LoadEmbedded()
            Dim usable = HardwareProbe.Detect(caps, 6000)
            Dim lines As New List(Of String)
            For Each encName In HardwareProbe.Candidates
                If caps.FindEncoder(encName) Is Nothing Then
                    lines.Add($"{encName}: 未编译进 ffmpeg")
                ElseIf usable.Contains(encName) Then
                    lines.Add($"{encName}: 可用")
                Else
                    lines.Add($"{encName}: 不可用")
                End If
            Next
            File.WriteAllLines(argv(hwIdx + 1), lines, System.Text.Encoding.UTF8)
            Application.Current.Shutdown(0)
            Return
        End If

        Dim evalIdx = Array.IndexOf(argv, "--eval")
        If evalIdx >= 0 Then
            RunEval(argv, evalIdx)
            Return
        End If

        Try
            _vm = New MainViewModel()
            _vm.Initialize(Capabilities.LoadEmbedded(), ModuleLibrary.LoadEmbedded())
            DataContext = _vm

            ' 显式同步一次下拉框选中项：绑定时序上 ItemsSource 可能晚于 SelectedItem，
            ' 那样 WPF 会把这个值丢掉，界面就显示成空框。
            EncoderCombo.SelectedItem = _vm.VideoEncoder
            ContainerCombo.SelectedItem = _vm.Container
            AudioCombo.SelectedItem = _vm.AudioEncoder

            Dim encIdx = Array.IndexOf(argv, "--enc")
            If encIdx >= 0 AndAlso encIdx + 1 < argv.Length Then
                Dim want = argv(encIdx + 1)
                If _vm.EncoderChoices.Contains(want) Then
                    _vm.VideoEncoder = want
                    EncoderCombo.SelectedItem = want
                End If
            End If

            ' --select <moduleId>：启动即选中某模块，便于截图/回归核对编辑面板
            Dim selIdx = Array.IndexOf(argv, "--select")
            If selIdx >= 0 AndAlso selIdx + 1 < argv.Length Then
                Dim wantId = argv(selIdx + 1)
                Dim card = _vm.VideoChain.FirstOrDefault(
                    Function(c) String.Equals(c.Instance.Definition.Id, wantId, StringComparison.OrdinalIgnoreCase))

                ' 不在默认链上的模块：先加进链再选中，方便自动化截图/验证
                If card Is Nothing Then
                    Dim def = _vm.Library.Find(wantId)
                    If def IsNot Nothing Then
                        _vm.AddToChain(def)
                        card = _vm.VideoChain.FirstOrDefault(
                            Function(c) String.Equals(c.Instance.Definition.Id, wantId, StringComparison.OrdinalIgnoreCase))
                    End If
                End If

                If card IsNot Nothing Then _vm.SelectedCard = card
            End If

            ' --mode av|video|audio：切换媒体模式，用于验证三种输出形态
            Dim modeIdx = Array.IndexOf(argv, "--mode")
            If modeIdx >= 0 AndAlso modeIdx + 1 < argv.Length Then
                Select Case argv(modeIdx + 1).ToLowerInvariant()
                    Case "video"
                        _vm.IncludesVideo = True : _vm.IncludesAudio = False
                    Case "audio"
                        _vm.IncludesVideo = False : _vm.IncludesAudio = True
                    Case Else
                        _vm.IncludesVideo = True : _vm.IncludesAudio = True
                End Select
            End If

            ' --add "a.mp4;b.mp3"：启动即导入文件（自动化测试用）
            Dim addIdx = Array.IndexOf(argv, "--add")
            If addIdx >= 0 AndAlso addIdx + 1 < argv.Length Then
                _vm.AddFiles(argv(addIdx + 1).Split(";"c))
            End If

            ' 硬件编码器探测：后台跑，不挡界面。探测期间下拉框里先照常显示。
            Dim needHwProbe = Array.IndexOf(argv, "--no-hwprobe") < 0
            If needHwProbe Then
                Dim _probe = _vm.DetectHardwareAsync()
            End If

            ' --add-module <id>：把模块加进链但不选中（可重复）
            Dim ai = 0
            While ai < argv.Length
                If argv(ai) = "--add-module" AndAlso ai + 1 < argv.Length Then
                    Dim adef = _vm.Library.Find(argv(ai + 1))
                    If adef IsNot Nothing Then _vm.AddToChain(adef)
                    ai += 2
                Else
                    ai += 1
                End If
            End While

            ' --remove <id>：从链上移除模块（可重复），测试互斥组合时用
            Dim ri = 0
            Dim anyRemove = False
            While ri < argv.Length
                If argv(ri) = "--remove" AndAlso ri + 1 < argv.Length Then
                    Dim rid = argv(ri + 1)
                    Dim target = _vm.VideoInstances.Concat(_vm.AudioInstances).FirstOrDefault(
                        Function(x) String.Equals(x.Definition.Id, rid, StringComparison.OrdinalIgnoreCase))
                    If target IsNot Nothing Then
                        If String.Equals(target.Definition.Scope, "audio", StringComparison.OrdinalIgnoreCase) Then
                            _vm.AudioInstances.Remove(target)
                        Else
                            _vm.VideoInstances.Remove(target)
                        End If
                        anyRemove = True
                    End If
                    ri += 2
                Else
                    ri += 1
                End If
            End While
            If anyRemove Then _vm.Rebuild()

            ' --set <moduleId> <key> <value>：可重复。用循环而不是 IndexOf，否则只会处理第一个
            Dim si = 0
            Dim anySet = False
            While si < argv.Length
                If argv(si) = "--set" AndAlso si + 3 < argv.Length Then
                    Dim mid = argv(si + 1) : Dim key = argv(si + 2) : Dim val = argv(si + 3)
                    Dim target = _vm.VideoInstances.Concat(_vm.AudioInstances).FirstOrDefault(
                        Function(x) String.Equals(x.Definition.Id, mid, StringComparison.OrdinalIgnoreCase))
                    If target IsNot Nothing Then
                        target.Values(key) = val
                        anySet = True
                    End If
                    si += 4
                Else
                    si += 1
                End If
            End While
            If anySet Then _vm.Rebuild()

            ' 输出目录 / 方案读写（自动化测试用）
            Dim outDirIdx = Array.IndexOf(argv, "--outdir")
            If outDirIdx >= 0 AndAlso outDirIdx + 1 < argv.Length Then
                _vm.OutputDirectory = argv(outDirIdx + 1)
            End If

            Dim loadIdx = Array.IndexOf(argv, "--load-preset")
            If loadIdx >= 0 AndAlso loadIdx + 1 < argv.Length Then
                PresetService.Load(_vm, argv(loadIdx + 1))
            End If

            Dim saveIdx = Array.IndexOf(argv, "--save-preset")
            If saveIdx >= 0 AndAlso saveIdx + 1 < argv.Length Then
                PresetService.Save(_vm, argv(saveIdx + 1))
            End If

            ' 放到最后：导出的必须是所有参数都处理完之后的状态
            Dim cmdOutIdx = Array.IndexOf(argv, "--command-out")
            If cmdOutIdx >= 0 AndAlso cmdOutIdx + 1 < argv.Length Then
                File.WriteAllText(argv(cmdOutIdx + 1), _vm.CommandText, System.Text.Encoding.UTF8)
            End If

            CapText.Text = $"内置 ffmpeg {_vm.Capabilities.FfmpegVersion.Split("-"c)(0)} · " &
                           $"{_vm.Capabilities.Encoders.Count} 编码器 · {_vm.Library.Modules.Count} 个参数模块"
        Catch ex As Exception
            CapText.Text = "能力表加载失败：" & ex.Message
        End Try

        ' --autorun：启动后自动跑完队列再截图，用于无人值守验证真实转码
        If Array.IndexOf(argv, "--autorun") >= 0 Then
            Dispatcher.BeginInvoke(New Action(Async Sub()
                                                  Await Task.Delay(300)
                                                  Await _vm.RunQueueAsync()
                                                  Dim si = Array.IndexOf(argv, "--shot")
                                                  If si >= 0 AndAlso si + 1 < argv.Length Then
                                                      Capture(argv(si + 1))
                                                  Else
                                                      Application.Current.Shutdown()
                                                  End If
                                              End Sub), DispatcherPriority.ApplicationIdle)
            Return
        End If

        Dim idx = Array.IndexOf(argv, "--shot")
        If idx < 0 OrElse idx + 1 >= argv.Length Then Return

        Dim outPath = argv(idx + 1)
        Dispatcher.BeginInvoke(New Action(Sub() Capture(outPath)), DispatcherPriority.ApplicationIdle)
    End Sub

    ''' <summary>点卡片 → 右侧切换到参数编辑。</summary>
    Private Sub Card_Click(sender As Object, e As MouseButtonEventArgs)
        Dim fe = TryCast(sender, FrameworkElement)
        Dim card = TryCast(fe?.DataContext, ModuleCardVm)
        If card IsNot Nothing AndAlso _vm IsNot Nothing Then
            _vm.SelectedCard = card
        End If
    End Sub

    ''' <summary>选择文件导入。</summary>
    Private Sub AddFiles_Click(sender As Object, e As RoutedEventArgs)
        If _vm Is Nothing Then Return
        Dim dlg As New Microsoft.Win32.OpenFileDialog() With {
            .Multiselect = True,
            .Title = "选择媒体文件",
            .Filter = "媒体文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.opus;*.wma|所有文件|*.*"
        }
        If dlg.ShowDialog() = True Then _vm.AddFiles(dlg.FileNames)
    End Sub

    ''' <summary>拖入文件。</summary>
    Private Sub Window_DragOver(sender As Object, e As DragEventArgs)
        e.Effects = If(e.Data.GetDataPresent(DataFormats.FileDrop), DragDropEffects.Copy, DragDropEffects.None)
        e.Handled = True
    End Sub

    Private Sub Window_Drop(sender As Object, e As DragEventArgs)
        If _vm Is Nothing Then Return
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        Dim files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If files IsNot Nothing Then _vm.AddFiles(files)
    End Sub

    ''' <summary>点击任务 → 整个界面跟随该任务的媒体模式。</summary>


    ' ---------- 卡片按钮：替代拖拽 ----------

    Private Shared Function CardOf(sender As Object) As ModuleCardVm
        Dim fe = TryCast(sender, FrameworkElement)
        Return TryCast(fe?.DataContext, ModuleCardVm)
    End Function

    Private Sub MoveUp_Click(sender As Object, e As RoutedEventArgs)
        If _vm IsNot Nothing Then _vm.MoveModule(CardOf(sender), -1)
    End Sub

    Private Sub MoveDown_Click(sender As Object, e As RoutedEventArgs)
        If _vm IsNot Nothing Then _vm.MoveModule(CardOf(sender), 1)
    End Sub

    Private Sub RemoveCard_Click(sender As Object, e As RoutedEventArgs)
        If _vm IsNot Nothing Then _vm.RemoveModule(CardOf(sender))
    End Sub

    ' ---------- 输出目录 ----------

    Private Sub PickOutputDir_Click(sender As Object, e As RoutedEventArgs)
        If _vm Is Nothing Then Return
        Dim dlg As New Microsoft.Win32.OpenFolderDialog() With {
            .Title = "选择输出目录",
            .Multiselect = False
        }
        If dlg.ShowDialog() = True AndAlso Not String.IsNullOrEmpty(dlg.FolderName) Then
            _vm.OutputDirectory = dlg.FolderName
        End If
    End Sub

    ' ---------- 方案 ----------

    Private Sub SavePreset_Click(sender As Object, e As RoutedEventArgs)
        If _vm Is Nothing Then Return
        Dim dlg As New Microsoft.Win32.SaveFileDialog() With {
            .Title = "保存方案",
            .Filter = "方案文件|*.json|所有文件|*.*",
            .DefaultExt = ".json",
            .FileName = "方案.json"
        }
        If dlg.ShowDialog() = True Then
            Try
                PresetService.Save(_vm, dlg.FileName)
            Catch ex As Exception
                MessageBox.Show("保存失败：" & ex.Message, "FFmpeg Studio")
            End Try
        End If
    End Sub

    Private Sub LoadPreset_Click(sender As Object, e As RoutedEventArgs)
        If _vm Is Nothing Then Return
        Dim dlg As New Microsoft.Win32.OpenFileDialog() With {
            .Title = "打开方案",
            .Filter = "方案文件|*.json|所有文件|*.*"
        }
        If dlg.ShowDialog() = True Then
            If Not PresetService.Load(_vm, dlg.FileName) Then
                MessageBox.Show("这个文件不是有效的方案。", "FFmpeg Studio")
            End If
        End If
    End Sub

    Private Sub Job_Click(sender As Object, e As MouseButtonEventArgs)
        Dim fe = TryCast(sender, FrameworkElement)
        Dim job = TryCast(fe?.DataContext, JobVm)
        If job IsNot Nothing AndAlso _vm IsNot Nothing Then _vm.ActiveJob = job
    End Sub

    ''' <summary>开始 / 取消队列。</summary>
    Private Async Sub RunQueue_Click(sender As Object, e As RoutedEventArgs)
        If _vm Is Nothing Then Return
        If _vm.IsRunning Then
            _vm.CancelQueue()
        Else
            Await _vm.RunQueueAsync()
        End If
    End Sub

    ''' <summary>返回模块库。</summary>
    Private Sub BackToLibrary_Click(sender As Object, e As RoutedEventArgs)
        If _vm IsNot Nothing Then _vm.Editing = False
    End Sub

    ''' <summary>预设值按钮。</summary>
    Private Sub Preset_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        Dim preset = TryCast(btn.Tag, PresetOption)
        If preset Is Nothing Then Return

        Dim vm = FindControlVm(btn)
        If vm IsNot Nothing Then vm.Value = preset.Value
    End Sub

    ''' <summary>从按钮往上找承载它的 ControlVm。</summary>
    Private Function FindControlVm(start As DependencyObject) As ControlVm
        Dim node = start
        While node IsNot Nothing
            Dim fe = TryCast(node, FrameworkElement)
            If fe IsNot Nothing AndAlso TypeOf fe.DataContext Is ControlVm Then
                Return CType(fe.DataContext, ControlVm)
            End If
            node = VisualTreeHelper.GetParent(node)
        End While
        Return Nothing
    End Function

    ''' <summary>模块库条目点击 → 加到链上。</summary>
    Private Sub LibraryItem_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        Dim item = TryCast(btn.Tag, LibraryItemVm)
        If item Is Nothing OrElse _vm Is Nothing Then Return
        _vm.AddToChain(item.Definition)
    End Sub

    ''' <summary>把窗口内容渲染成 PNG（2x），用于不依赖屏幕截图的 UI 核对。</summary>
    Private Sub Capture(outPath As String)
        UpdateLayout()

        Dim w = Root.ActualWidth
        Dim h = Root.ActualHeight
        If w <= 0 OrElse h <= 0 Then
            Application.Current.Shutdown(1)
            Return
        End If

        Const scale As Double = 2.0
        Dim rtb = New RenderTargetBitmap(
            CInt(w * scale), CInt(h * scale),
            96 * scale, 96 * scale,
            PixelFormats.Pbgra32)
        rtb.Render(Root)

        Dim encoder = New PngBitmapEncoder()
        encoder.Frames.Add(BitmapFrame.Create(rtb))
        Using fs = File.Create(outPath)
            encoder.Save(fs)
        End Using

        Application.Current.Shutdown()
    End Sub

    ''' <summary>求值器自检：把状态和命令打到控制台。</summary>
    Private Sub RunEval(argv As String(), evalIdx As Integer)
        Try
            Console.OutputEncoding = System.Text.Encoding.UTF8
        Catch
        End Try

        Dim enc = If(evalIdx + 1 < argv.Length, argv(evalIdx + 1), "libx264")
        Dim cont = If(evalIdx + 2 < argv.Length, argv(evalIdx + 2), "mp4")

        Dim caps = Capabilities.LoadEmbedded()
        Dim library = ModuleLibrary.LoadEmbedded()
        Dim evaluator As New ModuleEvaluator()

        ' 全部模块都挂上：既能验证每条 binding 都能求值，也能验证互斥关系
        Dim chain As New List(Of ModuleInstance)()
        For Each def In library.Modules
            chain.Add(def.CreateInstance())
        Next

        Dim ctx As New EvalContext With {
            .Capabilities = caps,
            .VideoEncoder = enc,
            .AudioEncoder = "aac",
            .ContainerName = cont,
            .IncludesVideo = True,
            .IncludesAudio = True,
            .Instances = chain
        }

        Console.WriteLine($"=== encoder={enc}  container={cont} ===")
        Console.WriteLine()
        Console.WriteLine("链上模块：")
        For Each inst In chain
            Dim ev = evaluator.Evaluate(inst.Definition, ctx)
            Console.WriteLine($"  [{ev.StateLabel.PadRight(8)}] {inst.Definition.Title.PadRight(10)} {inst.SummaryText().PadRight(14)} {ev.Reason}")
            For Each d In ev.Diagnostics
                Console.WriteLine($"              ! {d}")
            Next
        Next

        Console.WriteLine()
        Console.WriteLine("模块库：")
        For Each ev In evaluator.EvaluateAll(library, ctx)
            Console.WriteLine($"  [{ev.StateLabel.PadRight(8)}] {ev.Definition.Title.PadRight(10)} {ev.Reason}")
        Next

        Console.WriteLine()
        Console.WriteLine("生成命令：")
        Console.WriteLine("  " & evaluator.BuildCommand(ctx, "beach_4k.mp4", "out.mp4"))
        Console.WriteLine()

        Application.Current.Shutdown(0)
    End Sub

End Class
