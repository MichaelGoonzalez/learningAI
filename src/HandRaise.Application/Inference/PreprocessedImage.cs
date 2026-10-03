namespace HandRaise.Application.Inference;

public sealed record PreprocessedImage(float[] Tensor, LetterboxTransform Transform);
