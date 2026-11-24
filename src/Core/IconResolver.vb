Imports System.Windows.Media

''' <summary>把模块定义里的图标名解析成可复用的矢量 Geometry（24×24 坐标系）。</summary>
Public Module IconResolver

    Private ReadOnly Map As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"sliders", "M 4,7 L 13,7 M 19,7 L 20,7 M 16,5 A 2,2 0 1 0 16,9 A 2,2 0 1 0 16,5 M 4,17 L 7,17 M 13,17 L 20,17 M 10,15 A 2,2 0 1 0 10,19 A 2,2 0 1 0 10,15"},
        {"scale", "M 4,9 L 4,4 L 9,4 M 20,15 L 20,20 L 15,20 M 4,4 L 10,10 M 20,20 L 14,14"},
        {"clock", "M 20.5,12 A 8.5,8.5 0 1 1 3.5,12 A 8.5,8.5 0 1 1 20.5,12 M 12,7.5 L 12,12 L 15,14"},
        {"layers", "M 12,3 L 20,7.5 L 12,12 L 4,7.5 Z M 4,12 L 12,16.5 L 20,12 M 4,16.5 L 12,21 L 20,16.5"},
        {"gauge", "M 4,17 A 8,8 0 1 1 20,17 M 12,17 L 16,12"},
        {"bolt", "M 13,3 L 5,13.5 L 10.5,13.5 L 10,21 L 18,10.5 L 12.5,10.5 Z"},
        {"audio", "M 4,10 L 4,14 M 8,7 L 8,17 M 12,4.5 L 12,19.5 M 16,8 L 16,16 M 20,10.5 L 20,13.5"},
        {"tune", "M 12,3 L 12,9 M 12,15 L 12,21 M 6,9 L 6,15 M 6,3 L 6,6 M 6,18 L 6,21 M 18,9 L 18,15 M 18,3 L 18,6 M 18,18 L 18,21"},
        {"box", "M 12,3 L 20,7.5 L 20,16.5 L 12,21 L 4,16.5 L 4,7.5 Z M 4,7.5 L 12,12 L 20,7.5 M 12,12 L 12,21"},
        {"chip", "M 7,7 L 17,7 L 17,17 L 7,17 Z M 10,3.5 L 10,7 M 14,3.5 L 14,7 M 10,17 L 10,20.5 M 14,17 L 14,20.5 M 3.5,10 L 7,10 M 3.5,14 L 7,14 M 17,10 L 20.5,10 M 17,14 L 20.5,14"},
        {"palette", "M 12,3 A 9,9 0 1 0 12,21 C 13.5,21 14,20 13.5,19 C 13,18 13.5,17 15,17 L 17,17 A 4,4 0 0 0 21,13 C 21,8 17,3 12,3 Z"},
        {"crop", "M 7,3 L 7,17 L 21,17 M 3,7 L 17,7 L 17,21"},
        {"rotate", "M 20,12 A 8,8 0 1 0 12,20 M 20,12 L 20,6 M 20,12 L 14,12"},
        {"sparkle", "M 12,3 L 13.6,9 L 19,10.6 L 13.6,12.2 L 12,18 L 10.4,12.2 L 5,10.6 L 10.4,9 Z"},
        {"scissors", "M 6.5,10 A 2.5,2.5 0 1 0 6.5,5 A 2.5,2.5 0 1 0 6.5,10 M 6.5,14 A 2.5,2.5 0 1 0 6.5,19 A 2.5,2.5 0 1 0 6.5,14 M 8.6,8.4 L 20,19 M 20,5 L 8.6,15.6"},
        {"tag", "M 11.5,3.5 L 20,3.5 L 20,12 L 12,20 L 4,12 Z M 16.5,7.5 L 16.5,7.6"},
        {"layersoff", "M 12,3 L 20,7.5 L 12,12 L 4,7.5 Z M 4,12 L 12,16.5 L 20,12"},
        {"lock", "M 6,10.5 L 18,10.5 L 18,19.5 L 6,19.5 Z M 9,10.5 L 9,8 A 3,3 0 0 1 15,8 L 15,10.5"}
    }

    Private ReadOnly Cache As New Dictionary(Of String, Geometry)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly Gate As New Object()

    Public Function [Get](key As String) As Geometry
        Dim k = If(String.IsNullOrEmpty(key), "sliders", key)

        SyncLock Gate
            Dim cached As Geometry = Nothing
            If Cache.TryGetValue(k, cached) Then Return cached

            Dim data As String = Nothing
            If Not Map.TryGetValue(k, data) Then data = Map("sliders")

            Dim geo = Geometry.Parse(data)
            geo.Freeze()
            Cache(k) = geo
            Return geo
        End SyncLock
    End Function
End Module
