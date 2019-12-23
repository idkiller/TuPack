using Mono.Cecil;

namespace TuPack
{
    struct TracerFunctions
    {
        public MethodReference Begin;
        public MethodReference End;
        public MethodReference BeginAsync;
        public MethodReference EndAsync;
    }
}
