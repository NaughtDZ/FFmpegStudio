Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Threading
Imports System.Windows.Threading

''' <summary>轻量 INotifyPropertyChanged 基类。</summary>
Public MustInherit Class ObservableObject
    Implements INotifyPropertyChanged

    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

    Protected Sub [Raise](name As String)
        RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
    End Sub

    Protected Function SetField(Of T)(ByRef field As T, value As T, name As String) As Boolean
        If EqualityComparer(Of T).Default.Equals(field, value) Then Return False
        field = value
        [Raise](name)
        Return True
    End Function
End Class


''' <summary>链上的一张卡片。</summary>
Public Class ModuleCardVm
    Inherits ObservableObject

    Public Property Instance As ModuleInstance

    Private _evaluation As ModuleEvaluation

    Public Sub New(instance As ModuleInstance, evaluation As ModuleEvaluation)
        Me.Instance = instance
        _evaluation = evaluation
    End Sub

    ''' <summary>求值结果。就地替换（不重建卡片），避免编辑时界面闪烁。</summary>
    Public Property Evaluation As ModuleEvaluation
        Get
            Return _evaluation
        End Get
        Set(value As ModuleEvaluation)
            _evaluation = value
            [Raise](NameOf(Evaluation))
            [Raise](NameOf(State))
            [Raise](NameOf(StateText))
            [Raise](NameOf(Reason))
            [Raise](NameOf(Summary))
        End Set
    End Property

    Public ReadOnly Property Title As String
        Get
            Return Instance.Definition.Title
        End Get
    End Property

    Public ReadOnly Property Subtitle As String
        Get
            Return Instance.Definition.Subtitle
        End Get
    End Property

    Public ReadOnly Property Summary As String
        Get
            Return Instance.SummaryText()
        End Get
    End Property

    Public ReadOnly Property State As ModuleState
        Get
            Return _evaluation.State
        End Get
    End Property

    Public ReadOnly Property StateText As String
        Get
            Return _evaluation.StateLabel
        End Get
    End Property

    Public ReadOnly Property Reason As String
        Get
            Return _evaluation.Reason
        End Get
    End Property

    Public ReadOnly Property IconGeometry As Geometry
        Get
            Return IconResolver.[Get](Instance.Definition.Icon)
        End Get
    End Property
End Class


''' <summary>模块库里的一项。</summary>
Public Class LibraryItemVm

    Public Property Definition As ModuleDefinition
    Public Property Evaluation As ModuleEvaluation

    Public Sub New(def As ModuleDefinition, ev As ModuleEvaluation)
        Definition = def
        Evaluation = ev
    End Sub

    Public ReadOnly Property Title As String
        Get
            Return Definition.Title
        End Get
    End Property

    Public ReadOnly Property Subtitle As String
        Get
            Return Definition.Subtitle
        End Get
    End Property

    Public ReadOnly Property State As ModuleState
        Get
            Return Evaluation.State
        End Get
    End Property

    Public ReadOnly Property StateText As String
        Get
            Return Evaluation.StateLabel
        End Get
    End Property

    Public ReadOnly Property IconGeometry As Geometry
        Get
            Return IconResolver.[Get](Definition.Icon)
        End Get
    End Property
End Class


''' <summary>
''' 一个可编辑控件的视图模型。
'''
''' 刻意不继承任何 WPF 容器：它只是「值 + 元数据」，由 DataTemplate 决定长什么样。
''' 这样同一个编辑器既能放进右侧面板（当前），也能放进卡片内（未来就地展开），零改动。
''' </summary>
Public Class ControlVm
    Inherits ObservableObject

    Public Property Instance As ModuleInstance
    Public Property Def As ControlDefinition

    Private ReadOnly _onChanged As Action

    Public Sub New(instance As ModuleInstance, def As ControlDefinition, onChanged As Action)
        Me.Instance = instance
        Me.Def = def
        _onChanged = onChanged
    End Sub

    Public ReadOnly Property Label As String
        Get
            Return Def.Label
        End Get
    End Property

    Public ReadOnly Property ParamType As String
        Get
            Return Def.ParamType.ToLowerInvariant()
        End Get
    End Property

    Public ReadOnly Property Options As List(Of EnumOption)
        Get
            Return Def.Options
        End Get
    End Property

    Public ReadOnly Property Presets As List(Of PresetOption)
        Get
            Return Def.Presets
        End Get
    End Property

    Public ReadOnly Property HasPresets As Boolean
        Get
            Return Def.Presets IsNot Nothing AndAlso Def.Presets.Count > 0
        End Get
    End Property

    Public ReadOnly Property Minimum As Double
        Get
            Return If(Def.Min.HasValue, Def.Min.Value, 0)
        End Get
    End Property

    Public ReadOnly Property Maximum As Double
        Get
            Return If(Def.Max.HasValue, Def.Max.Value, 100)
        End Get
    End Property

    ''' <summary>字符串形态的值（所有控件最终都存在这里）。</summary>
    Public Property Value As String
        Get
            Dim v As String = Nothing
            If Instance.Values.TryGetValue(Def.Key, v) Then Return v
            Return ""
        End Get
        Set(newValue As String)
            Dim old As String = Nothing
            Instance.Values.TryGetValue(Def.Key, old)
            If old = newValue Then Return

            Instance.Values(Def.Key) = newValue
            [Raise](NameOf(Value))
            [Raise](NameOf(DoubleValue))
            _onChanged?.Invoke()
        End Set
    End Property

    ''' <summary>数字形态（滑块用）。</summary>
    Public Property DoubleValue As Double
        Get
            Dim d As Double
            If Double.TryParse(Value, d) Then Return d
            Return 0
        End Get
        Set(newValue As Double)
            Dim text = If(Def.ParamType.Equals("int", StringComparison.OrdinalIgnoreCase),
                          CInt(Math.Round(newValue)).ToString(),
                          newValue.ToString("0.##"))
            Value = text
        End Set
    End Property

    Public ReadOnly Property IsNumeric As Boolean
        Get
            Return ParamType = "int" OrElse ParamType = "float"
        End Get
    End Property
End Class



''' <summary>一个转码任务的状态。</summary>
Public Enum JobStatus
    Pending = 0
    Running = 1
    Done = 2
    Failed = 3
    Canceled = 4
End Enum

''' <summary>队列里的一个任务。</summary>
Public Class JobVm
    Inherits ObservableObject

    Public Property InputPath As String = ""
    Public Property OutputPath As String = ""
    Public Property Media As MediaInfo

    ''' <summary>本任务输出是否包含视频/音频——导入时按 ffprobe 探测结果初始化，可被全局开关覆盖。</summary>
    Public Property IncludesVideo As Boolean = True
    Public Property IncludesAudio As Boolean = True

    Private _isActive As Boolean
    Public Property IsActive As Boolean
        Get
            Return _isActive
        End Get
        Set(value As Boolean)
            SetField(_isActive, value, NameOf(IsActive))
        End Set
    End Property

    Private _status As JobStatus = JobStatus.Pending
    Private _percent As Integer
    Private _speed As String = ""
    Private _eta As String = "—"
    Private _message As String = ""
    Private _sizeText As String = "—"

    Public ReadOnly Property FileName As String
        Get
            Return IO.Path.GetFileName(InputPath)
        End Get
    End Property

    Public ReadOnly Property MediaText As String
        Get
            If Media Is Nothing Then Return ""
            Dim bits As New List(Of String)
            If Media.HasVideo Then bits.Add(Media.ResolutionText)
            If Media.HasAudio Then bits.Add("音频")
            If Media.DurationMs > 0 Then bits.Add(Media.DurationText)
            Return String.Join(" · ", bits)
        End Get
    End Property

    Public Property Status As JobStatus
        Get
            Return _status
        End Get
        Set(value As JobStatus)
            If SetField(_status, value, NameOf(Status)) Then
                [Raise](NameOf(StatusText))
                [Raise](NameOf(IsRunning))
                [Raise](NameOf(IsDone))
                [Raise](NameOf(IsFailed))
            End If
        End Set
    End Property

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _status = JobStatus.Running
        End Get
    End Property

    Public ReadOnly Property IsDone As Boolean
        Get
            Return _status = JobStatus.Done
        End Get
    End Property

    Public ReadOnly Property IsFailed As Boolean
        Get
            Return _status = JobStatus.Failed OrElse _status = JobStatus.Canceled
        End Get
    End Property

    Public ReadOnly Property StatusText As String
        Get
            Select Case _status
                Case JobStatus.Pending : Return "等待"
                Case JobStatus.Running : Return "进行中"
                Case JobStatus.Done : Return "完成"
                Case JobStatus.Failed : Return "失败"
                Case Else : Return "已取消"
            End Select
        End Get
    End Property

    Public Property Percent As Integer
        Get
            Return _percent
        End Get
        Set(value As Integer)
            SetField(_percent, value, NameOf(Percent))
        End Set
    End Property

    Public Property Speed As String
        Get
            Return _speed
        End Get
        Set(value As String)
            If SetField(_speed, value, NameOf(Speed)) Then [Raise](NameOf(DetailText))
        End Set
    End Property

    Public Property EtaText As String
        Get
            Return _eta
        End Get
        Set(value As String)
            If SetField(_eta, value, NameOf(EtaText)) Then [Raise](NameOf(DetailText))
        End Set
    End Property

    Public Property SizeText As String
        Get
            Return _sizeText
        End Get
        Set(value As String)
            SetField(_sizeText, value, NameOf(SizeText))
        End Set
    End Property

    Public Property Message As String
        Get
            Return _message
        End Get
        Set(value As String)
            SetField(_message, value, NameOf(Message))
        End Set
    End Property

    Public ReadOnly Property DetailText As String
        Get
            If _status = JobStatus.Running Then
                Return $"{_percent}% · {_speed} · 剩 {_eta}"
            ElseIf _status = JobStatus.Done Then
                Return _message
            ElseIf _status = JobStatus.Failed Then
                Return "失败"
            ElseIf _status = JobStatus.Canceled Then
                Return "已取消"
            Else
                Return "等待开始"
            End If
        End Get
    End Property
End Class


''' <summary>主视图模型。</summary>
Public Class MainViewModel
    Inherits ObservableObject

    Public Property Capabilities As Capabilities
    Public Property Library As ModuleLibrary

    Private ReadOnly _evaluator As New ModuleEvaluator()

    Public Property VideoChain As New ObservableCollection(Of ModuleCardVm)
    Public Property AudioChain As New ObservableCollection(Of ModuleCardVm)
    Public Property LibraryItems As New ObservableCollection(Of LibraryItemVm)
    Public Property EncoderChoices As New ObservableCollection(Of String)
    Public Property AudioEncoderChoices As New ObservableCollection(Of String)
    Public Property ContainerChoices As New ObservableCollection(Of String) From {"mp4", "mkv", "mov", "webm"}

    Private _videoEncoder As String = "libx264"
    Private _audioEncoder As String = "aac"
    Private _container As String = "mp4"
    Private _includesVideo As Boolean = True
    Private _includesAudio As Boolean = True
    Private _commandText As String = ""
    Private _statusText As String = ""

    Public Property VideoEncoder As String
        Get
            Return _videoEncoder
        End Get
        Set(value As String)
            If SetField(_videoEncoder, value, NameOf(VideoEncoder)) Then Rebuild()
        End Set
    End Property

    Public Property AudioEncoder As String
        Get
            Return _audioEncoder
        End Get
        Set(value As String)
            If SetField(_audioEncoder, value, NameOf(AudioEncoder)) Then Rebuild()
        End Set
    End Property

    Public Property Container As String
        Get
            Return _container
        End Get
        Set(value As String)
            If SetField(_container, value, NameOf(Container)) Then
                Raise(NameOf(OutputDirectoryDisplay))
                RecalculateOutputs()
            End If
        End Set
    End Property

    ''' <summary>输出是否包含视频流。</summary>
    Public Property IncludesVideo As Boolean
        Get
            Return _includesVideo
        End Get
        Set(value As Boolean)
            If SetField(_includesVideo, value, NameOf(IncludesVideo)) Then
                If _activeJob IsNot Nothing Then _activeJob.IncludesVideo = value
                Rebuild()
            End If
        End Set
    End Property

    ''' <summary>输出是否包含音频流。</summary>
    Public Property IncludesAudio As Boolean
        Get
            Return _includesAudio
        End Get
        Set(value As Boolean)
            If SetField(_includesAudio, value, NameOf(IncludesAudio)) Then
                If _activeJob IsNot Nothing Then _activeJob.IncludesAudio = value
                Rebuild()
            End If
        End Set
    End Property

    Public Property CommandText As String
        Get
            Return _commandText
        End Get
        Set(value As String)
            SetField(_commandText, value, NameOf(CommandText))
        End Set
    End Property

    Public Property StatusText As String
        Get
            Return _statusText
        End Get
        Set(value As String)
            SetField(_statusText, value, NameOf(StatusText))
        End Set
    End Property

    ' ---------------- 右侧编辑面板 ----------------

    Private _selectedCard As ModuleCardVm
    Private _editing As Boolean

    Public Property SelectedCard As ModuleCardVm
        Get
            Return _selectedCard
        End Get
        Set(value As ModuleCardVm)
            If SetField(_selectedCard, value, NameOf(SelectedCard)) Then
                SelectedDefinition = value?.Instance.Definition
                RebuildEditors()
                Editing = value IsNot Nothing
            End If
        End Set
    End Property

    Private _selectedDefinition As ModuleDefinition

    Public Property SelectedDefinition As ModuleDefinition
        Get
            Return _selectedDefinition
        End Get
        Set(value As ModuleDefinition)
            SetField(_selectedDefinition, value, NameOf(SelectedDefinition))
        End Set
    End Property

    ''' <summary>true = 右侧显示参数编辑；false = 显示模块库。</summary>
    Public Property Editing As Boolean
        Get
            Return _editing
        End Get
        Set(value As Boolean)
            If SetField(_editing, value, NameOf(Editing)) Then
                [Raise](NameOf(NotEditing))
            End If
        End Set
    End Property

    Public ReadOnly Property NotEditing As Boolean
        Get
            Return Not _editing
        End Get
    End Property

    Public Property EditorControls As New ObservableCollection(Of ControlVm)

    Public Property VideoInstances As New List(Of ModuleInstance)
    Public Property AudioInstances As New List(Of ModuleInstance)

    Public Sub Initialize(caps As Capabilities, library As ModuleLibrary)
        Capabilities = caps
        Me.Library = library

        BuildEncoderChoices()

        VideoInstances.Clear()
        For Each id In New String() {"video.quality.crf", "video.scale", "video.preset", "video.fps", "video.pixfmt"}
            Dim d = library.Find(id)
            If d IsNot Nothing Then VideoInstances.Add(d.CreateInstance())
        Next

        AudioInstances.Clear()
        For Each id In New String() {"audio.bitrate", "audio.loudnorm"}
            Dim d = library.Find(id)
            If d IsNot Nothing Then AudioInstances.Add(d.CreateInstance())
        Next

        Rebuild()
    End Sub

    Private Sub BuildEncoderChoices()
        ' 视频
        Dim vPreferred = New String() {"copy", "libx264", "libx265", "h264_nvenc", "hevc_nvenc",
                                       "h264_qsv", "hevc_qsv", "libsvtav1", "libvpx-vp9", "mpeg4"}
        Dim vExisting = New HashSet(Of String)(Capabilities.EncodersOfKind("V").Select(Function(e) e.Name))

        For Each n In vPreferred
            If n = "copy" Then
                EncoderChoices.Add(n)
            ElseIf vExisting.Contains(n) Then
                EncoderChoices.Add(n)
            End If
        Next
        For Each n In vExisting.Except(vPreferred).OrderBy(Function(x) x)
            EncoderChoices.Add(n)
        Next

        ' 音频
        Dim aPreferred = New String() {"copy", "aac", "libmp3lame", "libopus", "libvorbis",
                                       "flac", "pcm_s16le", "ac3", "libfdk_aac"}
        Dim aExisting = New HashSet(Of String)(Capabilities.EncodersOfKind("A").Select(Function(e) e.Name))

        For Each n In aPreferred
            If n = "copy" Then
                AudioEncoderChoices.Add(n)
            ElseIf aExisting.Contains(n) Then
                AudioEncoderChoices.Add(n)
            End If
        Next
        For Each n In aExisting.Except(aPreferred).OrderBy(Function(x) x)
            AudioEncoderChoices.Add(n)
        Next
    End Sub

    Public Function BuildContext(video As Boolean, audio As Boolean) As EvalContext
        ' 视频链在前、音频链在后；求值器按模块 scope 选对应编码器，顺序只影响命令里的参数先后
        Dim all As New List(Of ModuleInstance)()
        all.AddRange(VideoInstances)
        all.AddRange(AudioInstances)

        Return New EvalContext With {
            .Capabilities = Capabilities,
            .VideoEncoder = VideoEncoder,
            .AudioEncoder = AudioEncoder,
            .ContainerName = Container,
            .IncludesVideo = video,
            .IncludesAudio = audio,
            .Instances = all
        }
    End Function

    ''' <summary>重新求值。链上卡片就地更新（不重建），避免编辑时闪烁。</summary>
    Public Sub Rebuild()
        ' 有选中任务时，整个界面跟随该任务的媒体模式；否则用全局开关
        Dim vMode = If(_activeJob IsNot Nothing, _activeJob.IncludesVideo, IncludesVideo)
        Dim aMode = If(_activeJob IsNot Nothing, _activeJob.IncludesAudio, IncludesAudio)

        Dim ctx = BuildContext(vMode, aMode)

        UpdateChain(VideoChain, VideoInstances, ctx)
        UpdateChain(AudioChain, AudioInstances, ctx)

        LibraryItems.Clear()
        For Each ev In _evaluator.EvaluateAll(Library, ctx)
            If ev.State <> ModuleState.Hidden Then
                LibraryItems.Add(New LibraryItemVm(ev.Definition, ev))
            End If
        Next

        ' 有任务时用第一个任务的真实路径，否则用占位路径
        Dim demoIn = If(_activeJob IsNot Nothing, _activeJob.InputPath,
                        If(Jobs.Count > 0, Jobs(0).InputPath, "beach_4k.mp4"))
        Dim demoOut = If(_activeJob IsNot Nothing, _activeJob.OutputPath,
                         If(Jobs.Count > 0, Jobs(0).OutputPath, "out.mp4"))
        CommandText = _evaluator.BuildCommand(ctx, demoIn, demoOut)

        Dim vAvail = VideoChain.Where(Function(c) c.State = ModuleState.Available).Count()
        Dim aAvail = AudioChain.Where(Function(c) c.State = ModuleState.Available).Count()
        Dim mode = If(IncludesVideo AndAlso IncludesAudio, "视频+音频",
                      If(IncludesVideo, "仅视频", If(IncludesAudio, "仅音频", "无输出")))
        StatusText = $"{mode} · 视频 {VideoEncoder} / 音频 {AudioEncoder} · {Container} · " &
                     $"视频模块 {vAvail}/{VideoInstances.Count} 音频模块 {aAvail}/{AudioInstances.Count} · 库 {LibraryItems.Count} 项"

        If SelectedCard IsNot Nothing Then
            SelectedCard.Evaluation = _evaluator.Evaluate(SelectedCard.Instance.Definition, ctx)
        End If
    End Sub

    Private Sub UpdateChain(chain As ObservableCollection(Of ModuleCardVm),
                            instances As List(Of ModuleInstance),
                            ctx As EvalContext)
        If chain.Count = instances.Count Then
            For i = 0 To instances.Count - 1
                chain(i).Evaluation = _evaluator.Evaluate(instances(i).Definition, ctx)
            Next
        Else
            chain.Clear()
            For Each inst In instances
                chain.Add(New ModuleCardVm(inst, _evaluator.Evaluate(inst.Definition, ctx)))
            Next
        End If
    End Sub

    Private Sub RebuildEditors()
        EditorControls.Clear()
        If _selectedCard Is Nothing Then Return

        For Each c In _selectedCard.Instance.Definition.Controls
            EditorControls.Add(New ControlVm(_selectedCard.Instance, c, AddressOf OnControlChanged))
        Next
    End Sub

    ''' <summary>控件值变了：重算整条链 + 命令。滑块拖动会高频调用，但求值很轻。</summary>
    Private Sub OnControlChanged()
        Rebuild()
    End Sub

    Public Sub AddToChain(def As ModuleDefinition)
        Dim target = If(String.Equals(def.Scope, "audio", StringComparison.OrdinalIgnoreCase),
                        AudioInstances, VideoInstances)
        If target.Any(Function(i) i.Definition Is def) Then Return
        target.Add(def.CreateInstance())
        Rebuild()
    End Sub

    ' ================= 任务队列 =================

    Public Property Jobs As New ObservableCollection(Of JobVm)

    Private _parallelism As Integer = 2
    Public Property Parallelism As Integer
        Get
            Return _parallelism
        End Get
        Set(value As Integer)
            SetField(_parallelism, Math.Max(1, Math.Min(8, value)), NameOf(Parallelism))
        End Set
    End Property

    Private _isRunning As Boolean
    Public Property IsRunning As Boolean
        Get
            Return _isRunning
        End Get
        Set(value As Boolean)
            If SetField(_isRunning, value, NameOf(IsRunning)) Then
                [Raise](NameOf(RunButtonText))
            End If
        End Set
    End Property

    Public ReadOnly Property RunButtonText As String
        Get
            Return If(_isRunning, "取消", "开始队列")
        End Get
    End Property

    Private _activeJob As JobVm

    ''' <summary>当前选中的任务：中间参数链、右侧模块库、底部命令预览都跟随它。</summary>
    Public Property ActiveJob As JobVm
        Get
            Return _activeJob
        End Get
        Set(value As JobVm)
            If _activeJob Is value Then Return
            If _activeJob IsNot Nothing Then _activeJob.IsActive = False
            _activeJob = value
            If _activeJob IsNot Nothing Then
                _activeJob.IsActive = True
                _includesVideo = _activeJob.IncludesVideo
                _includesAudio = _activeJob.IncludesAudio
                [Raise](NameOf(IncludesVideo))
                [Raise](NameOf(IncludesAudio))
            End If
            Rebuild()
        End Set
    End Property

    Private _cts As CancellationTokenSource

    ''' <summary>开发期演示任务：用 _testmedia 里的素材。接入文件导入后由用户拖入的文件替换。</summary>
    Public Sub LoadDemoJobs(demoDir As String)
        If Not IO.Directory.Exists(demoDir) Then Return

        For Each name In New String() {"av.mp4", "audio.mp3"}
            Dim src = IO.Path.Combine(demoDir, name)
            If Not IO.File.Exists(src) Then Continue For

            Dim j As New JobVm With {
                .InputPath = src,
                .OutputPath = IO.Path.Combine(demoDir, "out", IO.Path.GetFileNameWithoutExtension(name) & "_out.mp4")
            }
            j.Media = MediaProbe.Probe(FfmpegLocator.Ffprobe, src)
            Jobs.Add(j)
        Next

        Rebuild()   ' 让命令预览用上真实路径
    End Sub

    ''' <summary>按并行度跑完所有等待中的任务。</summary>
    Public Async Function RunQueueAsync() As Task
        If _isRunning Then Return

        Dim pending = Jobs.Where(Function(j) j.Status = JobStatus.Pending).ToList()
        If pending.Count = 0 Then Return

        _cts = New CancellationTokenSource()
        IsRunning = True

        Try
            Using sem As New SemaphoreSlim(Parallelism)
                Dim tasks As New List(Of Task)
                For Each job In pending
                    Dim captured = job
                    tasks.Add(Task.Run(Async Function()
                                           Await sem.WaitAsync()
                                           Try
                                               Await RunOneAsync(captured, _cts.Token)
                                           Finally
                                               sem.Release()
                                           End Try
                                       End Function))
                Next
                Await Task.WhenAll(tasks)
            End Using
        Finally
            IsRunning = False
            _cts?.Dispose()
            _cts = Nothing
        End Try
    End Function

    Public Sub CancelQueue()
        _cts?.Cancel()
    End Sub

    Private Async Function RunOneAsync(job As JobVm, ct As CancellationToken) As Task
        ' 状态切换必须回到 UI 线程
        Await Application.Current.Dispatcher.InvokeAsync(Sub()
                                         job.Status = JobStatus.Running
                                         job.Percent = 0
                                         job.Message = ""
                                     End Sub)

        ' 每个任务按自己的媒体模式生成参数：纯音频文件绝不会带上 -c:v
        Dim ctx = BuildContext(job.IncludesVideo, job.IncludesAudio)

        Dim args As New List(Of String) From {"-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats"}
        args.AddRange(_evaluator.BuildArguments(ctx, job.InputPath, job.OutputPath))

        Try
            Dim outDir = IO.Path.GetDirectoryName(job.OutputPath)
            If Not String.IsNullOrEmpty(outDir) AndAlso Not IO.Directory.Exists(outDir) Then
                IO.Directory.CreateDirectory(outDir)
            End If
        Catch
        End Try

        Dim prog As New Progress(Of RunProgress)(
            Sub(p)
                Application.Current.Dispatcher.BeginInvoke(Sub()
                                           job.Percent = Math.Max(0, p.Percent)
                                           job.Speed = p.Speed
                                           job.EtaText = p.EtaText
                                           job.SizeText = p.SizeText
                                       End Sub)
            End Sub)

        Dim runner As New FfmpegRunner()
        Dim res = Await runner.RunAsync(FfmpegLocator.Ffmpeg, args, If(job.Media?.DurationMs, 0L), prog, ct)

        Await Application.Current.Dispatcher.InvokeAsync(Sub()
                                         If res.Canceled Then
                                             job.Status = JobStatus.Canceled
                                             job.Message = "已取消"
                                         ElseIf res.Success Then
                                             job.Status = JobStatus.Done
                                             job.Percent = 100
                                             job.Message = $"完成 · {res.ElapsedMs / 1000.0:0.0}s · {job.SizeText}"
                                         Else
                                             job.Status = JobStatus.Failed
                                             job.Message = res.ErrorText
                                         End If
                                     End Sub)
    End Function


    ' ================= 文件导入 =================

    Private Shared ReadOnly MediaExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".mpg", ".mpeg", ".m2ts",
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".ape", ".alac"
    }

    Public Shared Function IsMediaFile(path As String) As Boolean
        Return MediaExtensions.Contains(IO.Path.GetExtension(path))
    End Function

    ''' <summary>
    ''' 导入文件。每个文件用 ffprobe 探测真实流信息，据此初始化它的媒体模式——
    ''' 纯音频文件不会被误设成"仅视频"。
    ''' </summary>
    Public Function AddFiles(paths As IEnumerable(Of String)) As Integer
        Dim added = 0
        For Each raw In paths
            If IO.Directory.Exists(raw) Then
                For Each f In IO.Directory.GetFiles(raw)
                    If IsMediaFile(f) AndAlso AddOne(f) Then added += 1
                Next
            ElseIf IO.File.Exists(raw) AndAlso IsMediaFile(raw) Then
                If AddOne(raw) Then added += 1
            End If
        Next

        If added > 0 Then
            ActiveJob = Jobs(Jobs.Count - 1)
            Rebuild()
        End If

        Return added
    End Function

    Private Function AddOne(path As String) As Boolean
        If Jobs.Any(Function(j) String.Equals(j.InputPath, path, StringComparison.OrdinalIgnoreCase)) Then Return False

        Dim info = MediaProbe.Probe(FfmpegLocator.Ffprobe, path)

        Dim job As New JobVm With {
            .InputPath = path,
            .Media = info,
            .IncludesVideo = info.HasVideo,
            .IncludesAudio = info.HasAudio
        }
        job.OutputPath = BuildOutputPath(job)

        Jobs.Add(job)
        Return True
    End Function

    ''' <summary>把全局媒体模式套到所有任务（批量覆盖）。</summary>
    Public Sub ApplyModeToAllJobs()
        For Each j In Jobs
            j.IncludesVideo = IncludesVideo
            j.IncludesAudio = IncludesAudio
        Next
    End Sub

    Public Sub ClearFinishedJobs()
        For Each j In Jobs.Where(Function(x) x.Status <> JobStatus.Pending AndAlso x.Status <> JobStatus.Running).ToList()
            Jobs.Remove(j)
        Next
        Rebuild()
    End Sub


    ' ================= 输出目录 =================

    Private _outputDirectory As String = ""

    ' 输出根目录。空 = 每个文件自己旁边的 out\ 目录。
    Public Property OutputDirectory As String
        Get
            Return _outputDirectory
        End Get
        Set(value As String)
            If SetField(_outputDirectory, value, NameOf(OutputDirectory)) Then
                Raise(NameOf(OutputDirectoryDisplay))
                RecalculateOutputs()
            End If
        End Set
    End Property

    Public ReadOnly Property OutputDirectoryDisplay As String
        Get
            If String.IsNullOrEmpty(_outputDirectory) Then Return "（跟随输入文件）"
            Return _outputDirectory
        End Get
    End Property

    ' 按当前设置算出某个任务的输出路径。
    Public Function BuildOutputPath(job As JobVm) As String
        Dim dir As String
        If String.IsNullOrEmpty(_outputDirectory) Then
            Dim inDir = IO.Path.GetDirectoryName(job.InputPath)
            dir = If(String.IsNullOrEmpty(inDir), "out", IO.Path.Combine(inDir, "out"))
        Else
            dir = _outputDirectory
        End If

        ' 纯音频输出用音频容器扩展名，避免 mp4 里只有一个音轨的怪异产物
        Dim ext As String
        If job.IncludesVideo Then
            ext = "." & Container
        Else
            ext = If(Container = "mp4", ".m4a", ".mka")
        End If

        Return IO.Path.Combine(dir, IO.Path.GetFileNameWithoutExtension(job.InputPath) & "_out" & ext)
    End Function

    ' 输出目录 / 容器 / 媒体模式变化后，刷新所有还没开始的任务
    Public Sub RecalculateOutputs()
        For Each j In Jobs
            If j.Status <> JobStatus.Pending Then Continue For
            j.OutputPath = BuildOutputPath(j)
        Next
        Rebuild()
    End Sub

    ' ================= 方案套用 =================

    Public Sub ApplyPreset(data As PresetData)
        If data Is Nothing OrElse Library Is Nothing Then Return

        _videoEncoder = If(String.IsNullOrEmpty(data.VideoEncoder), _videoEncoder, data.VideoEncoder)
        _audioEncoder = If(String.IsNullOrEmpty(data.AudioEncoder), _audioEncoder, data.AudioEncoder)
        _container = If(String.IsNullOrEmpty(data.Container), _container, data.Container)
        _includesVideo = data.IncludesVideo
        _includesAudio = data.IncludesAudio

        Raise(NameOf(VideoEncoder))
        Raise(NameOf(AudioEncoder))
        Raise(NameOf(Container))
        Raise(NameOf(IncludesVideo))
        Raise(NameOf(IncludesAudio))

        VideoInstances.Clear()
        AudioInstances.Clear()

        For Each pm In data.Modules
            Dim def = Library.Find(pm.Id)
            If def Is Nothing Then Continue For      ' 方案里有、当前模块库没有的模块直接跳过
            Dim inst = def.CreateInstance()
            For Each kv In pm.Values
                If inst.Values.ContainsKey(kv.Key) Then inst.Values(kv.Key) = kv.Value
            Next
            If String.Equals(def.Scope, "audio", StringComparison.OrdinalIgnoreCase) Then
                AudioInstances.Add(inst)
            Else
                VideoInstances.Add(inst)
            End If
        Next

        RecalculateOutputs()
        Rebuild()
    End Sub

End Class
