namespace Game.Core
{
    /// <summary>
    /// Expected business failure (e.g. invalid input). Use exceptions only for programmer errors.
    /// </summary>
    public readonly struct Error
    {
        public string Code { get; }
        public string Message { get; }

        public Error(string code, string message)
        {
            Code = code;
            Message = message;
        }

        public override string ToString() => Code + ": " + Message;
    }

    /// <summary>
    /// Outcome of an operation that can fail in an expected way.
    /// </summary>
    public readonly struct Result<T>
    {
        private readonly T _value;

        public bool IsSuccess { get; }
        public Error Error { get; }

        public T Value
        {
            get
            {
                if (!IsSuccess)
                    throw new System.InvalidOperationException("Result has no value: " + Error);
                return _value;
            }
        }

        private Result(T value, bool isSuccess, Error error)
        {
            _value = value;
            IsSuccess = isSuccess;
            Error = error;
        }

        public static Result<T> Ok(T value) => new Result<T>(value, true, default);

        public static Result<T> Fail(string code, string message) =>
            new Result<T>(default, false, new Error(code, message));
    }
}
