namespace EngineTuner;

// Plain CMA-ES (Hansen's (mu/mu_w, lambda) with rank-one and rank-mu covariance updates and cumulative step-size
// adaptation). Minimizes. Dimension is small (tens), so the covariance eigendecomposition uses the Jacobi method.
public sealed class Cmaes
{
    public readonly int N;
    public readonly int Lambda;
    public readonly int Mu;
    private readonly double[] _weights;
    private readonly double _mueff, _cc, _cs, _c1, _cmu, _damps, _chiN;
    private readonly Random _rng;

    public double[] Mean;
    public double Sigma;
    private readonly double[,] _c;
    private readonly double[,] _b;
    private readonly double[] _d;
    private readonly double[] _pc;
    private readonly double[] _ps;
    private int _generation;

    public Cmaes(double[] mean, double sigma, int seed, int? lambda = null)
    {
        N = mean.Length;
        Mean = (double[])mean.Clone();
        Sigma = sigma;
        _rng = new Random(seed);
        Lambda = lambda ?? 4 + (int)(3 * Math.Log(N));
        Mu = Lambda / 2;
        _weights = new double[Mu];
        for (var i = 0; i < Mu; ++i)
            _weights[i] = Math.Log(Mu + 0.5) - Math.Log(i + 1);
        var sum = _weights.Sum();
        for (var i = 0; i < Mu; ++i)
            _weights[i] /= sum;
        _mueff = 1 / _weights.Sum(w => w * w);
        _cc = (4 + _mueff / N) / (N + 4 + 2 * _mueff / N);
        _cs = (_mueff + 2) / (N + _mueff + 5);
        _c1 = 2 / ((N + 1.3) * (N + 1.3) + _mueff);
        _cmu = Math.Min(1 - _c1, 2 * (_mueff - 2 + 1 / _mueff) / ((N + 2) * (N + 2) + _mueff));
        _damps = 1 + 2 * Math.Max(0, Math.Sqrt((_mueff - 1) / (N + 1)) - 1) + _cs;
        _chiN = Math.Sqrt(N) * (1 - 1.0 / (4 * N) + 1.0 / (21 * N * N));
        _c = new double[N, N];
        _b = new double[N, N];
        _d = new double[N];
        _pc = new double[N];
        _ps = new double[N];
        for (var i = 0; i < N; ++i)
        {
            _c[i, i] = 1;
            _b[i, i] = 1;
            _d[i] = 1;
        }
    }

    // samples lambda candidates: x = mean + sigma * B * D * z
    public double[][] Ask()
    {
        var res = new double[Lambda][];
        for (var k = 0; k < Lambda; ++k)
        {
            var z = new double[N];
            for (var i = 0; i < N; ++i)
                z[i] = Gaussian();
            var x = new double[N];
            for (var i = 0; i < N; ++i)
            {
                var acc = 0.0;
                for (var j = 0; j < N; ++j)
                    acc += _b[i, j] * _d[j] * z[j];
                x[i] = Mean[i] + Sigma * acc;
            }
            res[k] = x;
        }
        return res;
    }

    // fitness: lower is better
    public void Tell(double[][] xs, double[] fitness)
    {
        ++_generation;
        var order = Enumerable.Range(0, xs.Length).OrderBy(i => fitness[i]).ToArray();
        var old = (double[])Mean.Clone();
        Mean = new double[N];
        for (var k = 0; k < Mu; ++k)
            for (var i = 0; i < N; ++i)
                Mean[i] += _weights[k] * xs[order[k]][i];

        // C^(-1/2) (mean - old) / sigma
        var y = new double[N];
        for (var i = 0; i < N; ++i)
            y[i] = (Mean[i] - old[i]) / Sigma;
        var by = new double[N]; // B^T y
        for (var i = 0; i < N; ++i)
            for (var j = 0; j < N; ++j)
                by[i] += _b[j, i] * y[j];
        var invSqrtCy = new double[N];
        for (var i = 0; i < N; ++i)
            for (var j = 0; j < N; ++j)
                invSqrtCy[i] += _b[i, j] * by[j] / _d[j];

        for (var i = 0; i < N; ++i)
            _ps[i] = (1 - _cs) * _ps[i] + Math.Sqrt(_cs * (2 - _cs) * _mueff) * invSqrtCy[i];
        var psNorm = Math.Sqrt(_ps.Sum(v => v * v));
        var hsig = psNorm / Math.Sqrt(1 - Math.Pow(1 - _cs, 2 * _generation)) / _chiN < 1.4 + 2.0 / (N + 1) ? 1.0 : 0.0;
        for (var i = 0; i < N; ++i)
            _pc[i] = (1 - _cc) * _pc[i] + hsig * Math.Sqrt(_cc * (2 - _cc) * _mueff) * y[i];

        for (var i = 0; i < N; ++i)
        {
            for (var j = 0; j <= i; ++j)
            {
                var rankMu = 0.0;
                for (var k = 0; k < Mu; ++k)
                {
                    var xi = (xs[order[k]][i] - old[i]) / Sigma;
                    var xj = (xs[order[k]][j] - old[j]) / Sigma;
                    rankMu += _weights[k] * xi * xj;
                }
                var v = (1 - _c1 - _cmu) * _c[i, j]
                    + _c1 * (_pc[i] * _pc[j] + (1 - hsig) * _cc * (2 - _cc) * _c[i, j])
                    + _cmu * rankMu;
                _c[i, j] = _c[j, i] = v;
            }
        }
        Sigma *= Math.Exp(_cs / _damps * (psNorm / _chiN - 1));
        Decompose();
    }

    private void Decompose()
    {
        var a = (double[,])_c.Clone();
        var v = new double[N, N];
        for (var i = 0; i < N; ++i)
            v[i, i] = 1;
        for (var sweep = 0; sweep < 50; ++sweep)
        {
            var off = 0.0;
            for (var p = 0; p < N; ++p)
                for (var q = p + 1; q < N; ++q)
                    off += a[p, q] * a[p, q];
            if (off < 1e-20)
                break;
            for (var p = 0; p < N; ++p)
            {
                for (var q = p + 1; q < N; ++q)
                {
                    if (Math.Abs(a[p, q]) < 1e-15)
                        continue;
                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0)
                        t = 1;
                    var c = 1 / Math.Sqrt(t * t + 1);
                    var s = t * c;
                    for (var k = 0; k < N; ++k)
                    {
                        var akp = a[k, p];
                        var akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (var k = 0; k < N; ++k)
                    {
                        var apk = a[p, k];
                        var aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (var k = 0; k < N; ++k)
                    {
                        var vkp = v[k, p];
                        var vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
        }
        for (var i = 0; i < N; ++i)
        {
            _d[i] = Math.Sqrt(Math.Max(1e-20, a[i, i]));
            for (var j = 0; j < N; ++j)
                _b[i, j] = v[i, j];
        }
    }

    private double Gaussian()
    {
        var u1 = 1.0 - _rng.NextDouble();
        var u2 = _rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
