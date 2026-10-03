using HandRaise.Application.Inference;

namespace HandRaise.Application.Tracking;

/// <summary>
/// Implementación propia de ByteTrack: Kalman de velocidad constante y asociación IoU
/// en dos etapas. No contiene código de ports de terceros.
/// </summary>
public sealed class ByteTracker : ITracker
{
    private readonly TrackerOptions _options;
    private readonly List<Track> _tracks = [];
    private int _nextId = 1;
    private double? _lastTimestamp;

    public ByteTracker(TrackerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public TrackerUpdate Update(
        IReadOnlyList<PosePerson> detections,
        double timestampMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(detections);
        if (_lastTimestamp is { } previous && timestampMilliseconds < previous)
        {
            throw new ArgumentException("El timestamp del tracker debe ser monotónico.", nameof(timestampMilliseconds));
        }

        var elapsedFrames = _lastTimestamp is null
            ? 1d
            : Math.Clamp(
                (timestampMilliseconds - _lastTimestamp.Value) /
                    (1000d / _options.NominalFramesPerSecond),
                0.1,
                _options.LostTrackBufferFrames + 1d);
        _lastTimestamp = timestampMilliseconds;
        foreach (var track in _tracks)
        {
            track.Predict(elapsedFrames, _options);
        }

        var assignments = new int?[detections.Count];
        var high = Enumerable.Range(0, detections.Count)
            .Where(index => detections[index].Confidence >= _options.HighConfidenceThreshold)
            .ToArray();
        var low = Enumerable.Range(0, detections.Count)
            .Where(index => detections[index].Confidence >= _options.LowConfidenceThreshold &&
                detections[index].Confidence < _options.HighConfidenceThreshold)
            .ToArray();
        var unmatchedTracks = Enumerable.Range(0, _tracks.Count).ToHashSet();

        Associate(detections, high, unmatchedTracks, assignments, _options.FirstMatchIouThreshold);
        Associate(detections, low, unmatchedTracks, assignments, _options.SecondMatchIouThreshold);

        foreach (var trackIndex in unmatchedTracks)
        {
            _tracks[trackIndex].MissedFrames++;
        }

        foreach (var detectionIndex in high.Where(index => assignments[index] is null))
        {
            if (detections[detectionIndex].Confidence < _options.NewTrackThreshold)
            {
                continue;
            }

            var track = new Track(_nextId++, detections[detectionIndex].Box, _options);
            _tracks.Add(track);
            assignments[detectionIndex] = track.Id;
        }

        _tracks.RemoveAll(track => track.MissedFrames > _options.LostTrackBufferFrames);
        var people = detections.Select((detection, index) =>
            new TrackedPose(detection, assignments[index])).ToArray();
        return new TrackerUpdate(people, _tracks.Select(track => track.Id).ToHashSet());
    }

    public void Reset()
    {
        _tracks.Clear();
        _nextId = 1;
        _lastTimestamp = null;
    }

    private void Associate(
        IReadOnlyList<PosePerson> detections,
        IReadOnlyList<int> detectionIndices,
        HashSet<int> unmatchedTracks,
        int?[] assignments,
        float minimumIou)
    {
        while (true)
        {
            var bestIou = minimumIou;
            var bestTrack = -1;
            var bestDetection = -1;
            foreach (var trackIndex in unmatchedTracks)
            {
                foreach (var detectionIndex in detectionIndices)
                {
                    if (assignments[detectionIndex] is not null)
                    {
                        continue;
                    }

                    var iou = IntersectionOverUnion(_tracks[trackIndex].Box, detections[detectionIndex].Box);
                    if (iou > bestIou ||
                        (Math.Abs(iou - bestIou) < 1e-6 &&
                            (bestTrack < 0 || _tracks[trackIndex].Id < _tracks[bestTrack].Id)))
                    {
                        bestIou = iou;
                        bestTrack = trackIndex;
                        bestDetection = detectionIndex;
                    }
                }
            }

            if (bestTrack < 0)
            {
                return;
            }

            var track = _tracks[bestTrack];
            track.Update(detections[bestDetection].Box, _options);
            assignments[bestDetection] = track.Id;
            unmatchedTracks.Remove(bestTrack);
        }
    }

    private static float IntersectionOverUnion(BoundingBox first, BoundingBox second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = first.Area + second.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private sealed class Track
    {
        private readonly KalmanAxis _centerX;
        private readonly KalmanAxis _centerY;
        private readonly KalmanAxis _width;
        private readonly KalmanAxis _height;

        public Track(int id, BoundingBox box, TrackerOptions options)
        {
            Id = id;
            _centerX = new KalmanAxis((box.Left + box.Right) / 2, options.MeasurementNoise);
            _centerY = new KalmanAxis((box.Top + box.Bottom) / 2, options.MeasurementNoise);
            _width = new KalmanAxis(box.Width, options.MeasurementNoise);
            _height = new KalmanAxis(box.Height, options.MeasurementNoise);
            Box = box;
        }

        public int Id { get; }
        public BoundingBox Box { get; private set; }
        public int MissedFrames { get; set; }

        public void Predict(double delta, TrackerOptions options)
        {
            _centerX.Predict(delta, options);
            _centerY.Predict(delta, options);
            _width.Predict(delta, options);
            _height.Predict(delta, options);
            RefreshBox();
        }

        public void Update(BoundingBox box, TrackerOptions options)
        {
            _centerX.Update((box.Left + box.Right) / 2, options.MeasurementNoise);
            _centerY.Update((box.Top + box.Bottom) / 2, options.MeasurementNoise);
            _width.Update(box.Width, options.MeasurementNoise);
            _height.Update(box.Height, options.MeasurementNoise);
            MissedFrames = 0;
            RefreshBox();
        }

        private void RefreshBox()
        {
            var width = Math.Max(1, _width.Position);
            var height = Math.Max(1, _height.Position);
            Box = new BoundingBox(
                (float)(_centerX.Position - width / 2),
                (float)(_centerY.Position - height / 2),
                (float)(_centerX.Position + width / 2),
                (float)(_centerY.Position + height / 2));
        }
    }

    private sealed class KalmanAxis(double initial, double initialVariance)
    {
        private double _position = initial;
        private double _velocity;
        private double _p00 = initialVariance;
        private double _p01;
        private double _p10;
        private double _p11 = initialVariance;

        public double Position => _position;

        public void Predict(double delta, TrackerOptions options)
        {
            _position += delta * _velocity;
            var p00 = _p00 + delta * (_p10 + _p01) + delta * delta * _p11 +
                options.PositionProcessNoise;
            var p01 = _p01 + delta * _p11;
            var p10 = _p10 + delta * _p11;
            var p11 = _p11 + options.VelocityProcessNoise;
            _p00 = p00;
            _p01 = p01;
            _p10 = p10;
            _p11 = p11;
        }

        public void Update(double measurement, double measurementNoise)
        {
            var residual = measurement - _position;
            var innovation = _p00 + measurementNoise;
            var k0 = _p00 / innovation;
            var k1 = _p10 / innovation;
            _position += k0 * residual;
            _velocity += k1 * residual;
            var p00 = (1 - k0) * _p00;
            var p01 = (1 - k0) * _p01;
            _p10 -= k1 * _p00;
            _p11 -= k1 * _p01;
            _p00 = p00;
            _p01 = p01;
        }
    }
}
