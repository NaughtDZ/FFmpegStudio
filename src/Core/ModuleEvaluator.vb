''' <summary>模块在当前状态下的可用性。</summary>
Public Enum ModuleState
    ''' <summary>可用：能插入、会写入命令。</summary>
    Available = 0
    ''' <summary>条件可用：换个编码器/容器就能用；留在链上但不写命令。</summary>
    Conditional = 1
    ''' <summary>已停用：与链上已有模块冲突，就地标灰并写明冲突源。</summary>
    Disabled = 2
    ''' <summary>不适用：当前组合下根本没有意义，模块库里不列出。</summary>
    Hidden = 3
End Enum


''' <summary>一次求值的结果。</summary>
Public Class ModuleEvaluation

    Public Property Definition As ModuleDefinition
    Public Property State As ModuleState = ModuleState.Hidden
    Public Property Reason As String = ""
    Public Property Binding As BindingDefinition
    Public Property Tokens As New List(Of String)()
    Public Property Diagnostics As New List(Of String)()

    Public ReadOnly Property IsEmitting As Boolean
        Get
            Return State = ModuleState.Available AndAlso Tokens.Count > 0
        End Get
    End Property

    Public ReadOnly Property StateLabel As String
        Get
            Select Case State
                Case ModuleState.Available : Return "可用"
                Case ModuleState.Conditional : Return "条件可用"
                Case ModuleState.Disabled : Return "已停用"
                Case Else : Return "不适用"
            End Select
        End Get
    End Property
End Class


''' <summary>
''' 求值上下文：当前的视频编码器、音频编码器、容器、媒体模式，以及链上已有的模块。
'''
''' 关键点：<b>视频模块看视频编码器，音频模块看音频编码器</b>。
''' 早先版本让音频模块也去看视频编码器，导致 -b:a 之类在任何视频编码器下都「可用」，
''' 而命令里根本没有 -c:a。
''' </summary>
Public Class EvalContext

    Public Property Capabilities As Capabilities
    Public Property VideoEncoder As String = ""
    Public Property AudioEncoder As String = ""
    Public Property ContainerName As String = ""

    ''' <summary>输出是否包含视频流。</summary>
    Public Property IncludesVideo As Boolean = True
    ''' <summary>输出是否包含音频流。</summary>
    Public Property IncludesAudio As Boolean = True

    Public Property Instances As New List(Of ModuleInstance)()

    ''' <summary>按模块作用域取对应的编码器名。</summary>
    Public Function EncoderNameFor(scope As String) As String
        If String.Equals(scope, "audio", StringComparison.OrdinalIgnoreCase) Then Return AudioEncoder
        Return VideoEncoder
    End Function

    Public Function EncoderInfoFor(scope As String) As EncoderInfo
        If Capabilities Is Nothing Then Return Nothing
        Return Capabilities.FindEncoder(EncoderNameFor(scope))
    End Function

    ''' <summary>取链上该模块的实例；没有则用默认值临时造一个（供模块库预览）。</summary>
    Public Function GetInstance(def As ModuleDefinition) As ModuleInstance
        Dim found = Instances.FirstOrDefault(Function(i) i.Definition Is def)
        If found IsNot Nothing Then Return found
        Return def.CreateInstance()
    End Function

    Public Function IsPresent(moduleId As String) As Boolean
        Return Instances.Any(Function(i) i.IsEnabled AndAlso
                             String.Equals(i.Definition.Id, moduleId, StringComparison.OrdinalIgnoreCase))
    End Function
End Class


''' <summary>
''' 兼容性求值器：把「模块定义 × 当前编码器/容器/已选模块」算成一个状态。
''' </summary>
Public Class ModuleEvaluator

    ''' <summary>
    ''' 输出层 / 输入层的全局选项白名单。它们不属于任何编码器，
    ''' 所以不能用 `ffmpeg -h encoder=X` 的能力表去校验它们是否存在。
    ''' </summary>
    Private Shared ReadOnly GlobalFlags As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "-i", "-r", "-pix_fmt", "-s", "-b:v", "-b:a", "-ar", "-ac", "-vf", "-af",
        "-c:v", "-c:a", "-f", "-movflags", "-ss", "-to", "-t", "-map", "-metadata",
        "-threads", "-filter_complex", "-q:v", "-q:a", "-y", "-vn", "-an",
        "-g", "-bf", "-profile:v", "-colorspace", "-ac", "-acodec", "-vcodec"
    }

    ''' <summary>对单个模块求值。</summary>
    Public Function Evaluate(def As ModuleDefinition, ctx As EvalContext) As ModuleEvaluation

        Dim result As New ModuleEvaluation With {.Definition = def}

        ' 0) 媒体模式：关掉的流，其所有模块都不适用
        If String.Equals(def.Scope, "video", StringComparison.OrdinalIgnoreCase) AndAlso Not ctx.IncludesVideo Then
            result.State = ModuleState.Hidden
            result.Reason = "已关闭视频流"
            Return result
        End If
        If String.Equals(def.Scope, "audio", StringComparison.OrdinalIgnoreCase) AndAlso Not ctx.IncludesAudio Then
            result.State = ModuleState.Hidden
            result.Reason = "已关闭音频流"
            Return result
        End If

        Dim encName = ctx.EncoderNameFor(def.Scope)
        Dim enc = ctx.EncoderInfoFor(def.Scope)

        ' 1) 编码器黑名单（如 copy）
        If def.ExcludeEncoders.Any(Function(e) String.Equals(e, encName, StringComparison.OrdinalIgnoreCase)) Then
            result.State = ModuleState.Hidden
            result.Reason = $"{encName} 模式下不可用"
            Return result
        End If

        ' 2) 找匹配的 binding；顺便记下「只差编码器」的那条，用来说明为什么是条件可用
        Dim hit As BindingDefinition = Nothing
        Dim nearMiss As BindingDefinition = Nothing

        Dim probe = ctx.GetInstance(def)

        For Each b In def.Bindings
            If MatchesEncoder(b.Condition, ctx, def.Scope) AndAlso MatchesRest(b.Condition, ctx, def.Scope) Then
                ' RunIf：一个模块可能有多种形态，只有值对上的那条 binding 才产出
                If b.RunIf IsNot Nothing AndAlso Not ValuesMatch(b.RunIf, probe) Then Continue For
                hit = b
                Exit For
            ElseIf MatchesRest(b.Condition, ctx, def.Scope) AndAlso Not MatchAnyEncoder(b.Condition) Then
                If nearMiss Is Nothing Then nearMiss = b
            End If
        Next

        If hit Is Nothing Then
            If nearMiss IsNot Nothing Then
                result.State = ModuleState.Conditional
                result.Reason = "需要 " & String.Join(" / ", nearMiss.Condition.Encoder) & " 编码器"
            Else
                result.State = ModuleState.Hidden
                result.Reason = $"当前编码器 {encName} 不支持此参数"
            End If
            Return result
        End If

        ' 2.5) 前置依赖：两遍编码需要目标码率，否则 x264 会报 CRF/CQP is incompatible with 2pass
        For Each reqId In def.Requires
            If Not ctx.IsPresent(reqId) Then
                Dim need = ctx.Capabilities IsNot Nothing AndAlso String.IsNullOrEmpty(reqId)
                Dim label = reqId
                ' 把 id 换成人能看懂的名字
                Select Case reqId
                    Case "video.quality.bitrate" : label = "目标码率"
                    Case "video.quality.crf" : label = "质量控制"
                    Case "audio.bitrate" : label = "音频比特率"
                End Select
                result.State = ModuleState.Conditional
                result.Reason = "需要先添加「" & label & "」模块"
                result.Binding = hit
                Return result
            End If
        Next

        ' 3) 与链上已有模块的冲突（按 scope 隔离：视频模块不与音频模块冲突）
        For Each cid In def.Conflicts
            If ctx.IsPresent(cid) Then
                Dim other = ctx.Instances.First(Function(i) String.Equals(i.Definition.Id, cid, StringComparison.OrdinalIgnoreCase))
                result.State = ModuleState.Disabled
                result.Reason = $"与「{other.Definition.Title}」冲突"
                result.Binding = hit
                Return result
            End If
        Next

        ' 4) 编码器是否真的提供这些 flag（ffmpeg 升级后参数改名会在这里暴露）
        If enc IsNot Nothing Then
            For Each tpl In hit.Emit
                If tpl.StartsWith("-") AndAlso tpl.Length > 1 AndAlso
                   Not GlobalFlags.Contains(tpl) AndAlso
                   Not enc.SupportsFlag(tpl) Then
                    result.Diagnostics.Add($"{enc.Name} 的能力表中没有 {tpl}，可能已过时")
                End If
            Next
        End If

        ' 5) 生成输出 token
        Dim inst = ctx.GetInstance(def)
        result.Binding = hit

        ' SkipIf 命中（如「保持原始帧率」）：模块本身仍然可用，只是不产生任何参数。
        If hit.SkipIf IsNot Nothing Then
            For Each kv In hit.SkipIf
                Dim v As String = Nothing
                If inst.Values.TryGetValue(kv.Key, v) AndAlso String.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase) Then
                    result.State = ModuleState.Available
                    result.Reason = $"{kv.Key} = {v}，保持原值"
                    Return result
                End If
            Next
        End If

        For Each tpl In hit.Emit
            result.Tokens.Add(Resolve(tpl, inst, hit))
        Next

        result.State = ModuleState.Available
        Return result
    End Function

    ''' <summary>对整个模块库求值（模块库面板用）。</summary>
    Public Function EvaluateAll(library As ModuleLibrary, ctx As EvalContext) As List(Of ModuleEvaluation)
        Dim list As New List(Of ModuleEvaluation)()
        For Each def In library.Modules
            list.Add(Evaluate(def, ctx))
        Next
        Return list
    End Function

    ''' <summary>
    ''' 按链顺序生成完整命令行。
    '''
    ''' 三种媒体模式的结果完全不同：
    '''   视频+音频 → -c:v X ... -c:a Y ...
    '''   仅视频     → -c:v X ... -an
    '''   仅音频     → -vn -c:a Y ...        （绝不输出 -c:v / -vf）
    ''' </summary>
    Public Function BuildCommand(ctx As EvalContext, inputPath As String, outputPath As String) As String
        Return "ffmpeg " & String.Join(" ", BuildArguments(ctx, inputPath, outputPath).Select(Function(a) Quote(a)))
    End Function

    ''' <summary>
    ''' 生成 ffmpeg 的参数列表（不含可执行文件名，也不含 -progress 之类运行期开关）。
    ''' 用列表而不是拼字符串：交给 ProcessStartInfo.ArgumentList 可以彻底避开引号/空格转义问题。
    ''' </summary>
    Public Function BuildArguments(ctx As EvalContext, inputPath As String, outputPath As String) As List(Of String)
        Dim parts As New List(Of String) From {"-i", inputPath}

        Dim vfilters As New List(Of String)()
        Dim afilters As New List(Of String)()

        If ctx.IncludesVideo AndAlso Not String.IsNullOrEmpty(ctx.VideoEncoder) Then
            parts.Add("-c:v")
            parts.Add(ctx.VideoEncoder)
        End If
        If ctx.IncludesAudio AndAlso Not String.IsNullOrEmpty(ctx.AudioEncoder) Then
            parts.Add("-c:a")
            parts.Add(ctx.AudioEncoder)
        End If

        For Each inst In ctx.Instances
            If Not inst.IsEnabled Then Continue For

            Dim ev = Evaluate(inst.Definition, ctx)
            If ev.State <> ModuleState.Available Then Continue For

            For Each tok In ev.Tokens
                If inst.Definition.IsFilter Then
                    If String.Equals(inst.Definition.Scope, "audio", StringComparison.OrdinalIgnoreCase) Then
                        afilters.Add(tok)
                    Else
                        vfilters.Add(tok)
                    End If
                Else
                    parts.Add(tok)
                End If
            Next
        Next

        ' 同一路流的滤镜必须合并成一个 -vf / -af，否则前面的会被覆盖
        If ctx.IncludesVideo Then
            If vfilters.Count > 0 Then
                parts.Add("-vf")
                parts.Add(String.Join(",", vfilters))
            End If
        Else
            parts.Add("-vn")
        End If

        If ctx.IncludesAudio Then
            If afilters.Count > 0 Then
                parts.Add("-af")
                parts.Add(String.Join(",", afilters))
            End If
        Else
            parts.Add("-an")
        End If

        parts.Add(outputPath)
        Return parts
    End Function

    ' ---------------- 内部 ----------------

    Private Shared Function MatchesEncoder(c As ConditionDefinition, ctx As EvalContext, scope As String) As Boolean
        If c Is Nothing OrElse c.Encoder Is Nothing OrElse c.Encoder.Count = 0 Then Return True
        Dim name = ctx.EncoderNameFor(scope)
        Return c.Encoder.Any(Function(e) e = "*" OrElse String.Equals(e, name, StringComparison.OrdinalIgnoreCase))
    End Function

    Private Shared Function MatchAnyEncoder(c As ConditionDefinition) As Boolean
        Return c IsNot Nothing AndAlso c.Encoder IsNot Nothing AndAlso
               c.Encoder.Any(Function(e) e = "*")
    End Function

    Private Shared Function MatchesRest(c As ConditionDefinition, ctx As EvalContext, scope As String) As Boolean
        If c Is Nothing Then Return True

        If c.Container IsNot Nothing AndAlso c.Container.Count > 0 Then
            If Not c.Container.Any(Function(x) String.Equals(x, ctx.ContainerName, StringComparison.OrdinalIgnoreCase)) Then
                Return False
            End If
        End If

        If Not String.IsNullOrEmpty(c.RequiresFlag) Then
            Dim enc = ctx.EncoderInfoFor(scope)
            If enc Is Nothing OrElse Not enc.SupportsFlag(c.RequiresFlag) Then Return False
        End If

        Return True
    End Function

    Private Shared Function ValuesMatch(rule As Dictionary(Of String, String), inst As ModuleInstance) As Boolean
        For Each kv In rule
            Dim v As String = Nothing
            If Not inst.Values.TryGetValue(kv.Key, v) Then Return False
            If Not String.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase) Then Return False
        Next
        Return True
    End Function

    Private Shared Function Resolve(template As String, inst As ModuleInstance, b As BindingDefinition) As String
        Dim text = template
        For Each kv In inst.Values
            Dim value = kv.Value
            Dim mapped As String = Nothing
            If b.ValueMap IsNot Nothing AndAlso b.ValueMap.TryGetValue(value, mapped) Then
                value = mapped
            End If
            text = text.Replace("{" & kv.Key & "}", value)
        Next
        Return text
    End Function

    Private Shared Function Quote(s As String) As String
        If s Is Nothing Then Return """"""
        ' 以 - 开头的是 flag，永远不加引号（-c:v 里含冒号，否则会被包起来）
        If s.StartsWith("-") AndAlso s.Length > 1 Then Return s
        ' 只有空格和逗号会让命令行解析出错；冒号、等号在 shell 里是普通字符
        If s.Contains(" ") OrElse s.Contains(",") OrElse s.Contains("""") Then
            Return """" & s & """"
        End If
        Return s
    End Function
End Class
