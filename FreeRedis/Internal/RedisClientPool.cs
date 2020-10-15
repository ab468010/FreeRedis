﻿using FreeRedis.Internal.ObjectPool;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.IO;

namespace FreeRedis.Internal
{
    public class RedisClientPool : ObjectPool<RedisClient>
    {
        internal class RedisSocketScope : IRedisSocket
        {
            Object<RedisClient> _cli;
            RedisClientPool _pool;
            Exception _exception;
            IRedisSocket _rds => _cli.Value._singleRedisSocket;
            public RedisSocketScope(Object<RedisClient> cli, RedisClientPool pool)
            {
                _cli = cli;
                _pool = pool;
            }

            public void Dispose()
            {
                _pool.Return(_cli, _exception);
            }

            public string Host => _rds.Host;
            public bool Ssl => _rds.Ssl;
            public TimeSpan ConnectTimeout { get => _rds.ConnectTimeout; set => _rds.ConnectTimeout = value; }
            public TimeSpan ReceiveTimeout { get => _rds.ReceiveTimeout; set => _rds.ReceiveTimeout = value; }
            public TimeSpan SendTimeout { get => _rds.SendTimeout; set => _rds.SendTimeout = value; }
            public Socket Socket => _rds.Socket;
            public Stream Stream => _rds.Stream;
            public bool IsConnected => _rds.IsConnected;
            public RedisProtocol Protocol { get => _rds.Protocol; set => _rds.Protocol = value; }
            public Encoding Encoding { get => _rds.Encoding; set => _rds.Encoding = value; }
            public event EventHandler<EventArgs> Connected { add { _rds.Connected += value; } remove { _rds.Connected -= value; } }

            public RedisClient Client => _rds.Client;

            public void Connect() => _rds.Connect();
#if net40
#else
            public Task ConnectAsync() => _rds.ConnectAsync();
#endif
            public RedisResult<T> Read<T>() => _rds.Read<T>();
            public RedisResult<T> Read<T>(Encoding encoding) => _rds.Read<T>(encoding);
            public void ReadChunk(Stream destination, int bufferSize = 1024) => _rds.ReadChunk(destination, bufferSize);
            public void ResetHost(string host) => _rds.ResetHost(host);
            public void Write(CommandBuilder cmd) => _rds.Write(cmd);
            public void Write(Encoding encoding, CommandBuilder cmd) => _rds.Write(encoding, cmd);
        }

        public RedisClientPool(string connectionString, Action<RedisClient> connected) : base(null)
        {
            _policy = new RedisClientPoolPolicy
            {
                _pool = this
            };
            _policy.Connected += (s, o) =>
            {
                var cli = s as RedisClient;
                using (cli.NoneRedisSimpleError())
                {
                    cli._singleRedisSocket.Socket.ReceiveTimeout = (int)_policy._connectionStringBuilder.ReceiveTimeout.TotalMilliseconds;
                    cli._singleRedisSocket.Socket.SendTimeout = (int)_policy._connectionStringBuilder.SendTimeout.TotalMilliseconds;
                    cli._singleRedisSocket.Encoding = _policy._connectionStringBuilder.Encoding;

                    if (_policy._connectionStringBuilder.Protocol == RedisProtocol.RESP3)
                    {
                        cli.Hello("3", _policy._connectionStringBuilder.User, _policy._connectionStringBuilder.Password, _policy._connectionStringBuilder.ClientName);
                        if (cli.RedisSimpleError != null)
                            throw cli.RedisSimpleError;
                        cli._singleRedisSocket.Protocol = RedisProtocol.RESP3;
                    }
                    else if (!string.IsNullOrEmpty(_policy._connectionStringBuilder.User) && !string.IsNullOrEmpty(_policy._connectionStringBuilder.Password))
                    {
                        cli.Auth(_policy._connectionStringBuilder.User, _policy._connectionStringBuilder.Password);
                        if (cli.RedisSimpleError != null) 
                            throw cli.RedisSimpleError;
                    }
                    else if (!string.IsNullOrEmpty(_policy._connectionStringBuilder.Password))
                    {
                        cli.Auth(_policy._connectionStringBuilder.Password);
                        if (cli.RedisSimpleError != null && cli.RedisSimpleError.Message != "ERR Client sent AUTH, but no password is set")
                            throw cli.RedisSimpleError;
                    }

                    if (_policy._connectionStringBuilder.Database > 0)
                    {
                        cli.Select(_policy._connectionStringBuilder.Database);
                        if (cli.RedisSimpleError != null)
                            throw cli.RedisSimpleError;
                    }
                    if (!string.IsNullOrEmpty(_policy._connectionStringBuilder.ClientName) && _policy._connectionStringBuilder.Protocol == RedisProtocol.RESP2)
                    {
                        cli.ClientSetName(_policy._connectionStringBuilder.ClientName);
                        if (cli.RedisSimpleError != null)
                            throw cli.RedisSimpleError;
                    }
                }
                connected?.Invoke(cli);
            };
            this.Policy = _policy;
            _policy.ConnectionString = connectionString;
        }

        public void Return(Object<RedisClient> obj, Exception exception, bool isRecreate = false)
        {
            if (exception != null)
            {
                try
                {
                    try
                    {
                        obj.Value.Ping();

                        var fcolor = Console.ForegroundColor;
                        Console.WriteLine($"");
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"FreeRedis 错误【{Policy.Name}】：{exception.Message} {exception.StackTrace}");
                        Console.ForegroundColor = fcolor;
                        Console.WriteLine($"");
                    }
                    catch
                    {
                        obj.ResetValue();
                        obj.Value.Ping();
                    }
                }
                catch (Exception ex)
                {
                    base.SetUnavailable(ex);
                }
            }
            base.Return(obj, isRecreate);
        }

        internal bool CheckAvailable() => base.LiveCheckAvailable();

        internal RedisClientPoolPolicy _policy;
        public string Key => _policy.Key;
        public string Prefix => _policy._connectionStringBuilder.Prefix;
    }

    public class RedisClientPoolPolicy : IPolicy<RedisClient>
    {
        internal RedisClientPool _pool;
        internal ConnectionStringBuilder _connectionStringBuilder = new ConnectionStringBuilder();
        internal string Key => $"{_connectionStringBuilder.Host}/{_connectionStringBuilder.Database}";
        public event EventHandler Connected;

        public string Name { get => Key; set { throw new Exception("RedisClientPoolPolicy 不提供设置 Name 属性值。"); } }
        public int PoolSize { get => _connectionStringBuilder.MaxPoolSize; set => _connectionStringBuilder.MaxPoolSize = value; }
        public TimeSpan IdleTimeout { get => _connectionStringBuilder.IdleTimeout; set => _connectionStringBuilder.IdleTimeout = value; }
        public TimeSpan SyncGetTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public int AsyncGetCapacity { get; set; } = 100000;
        public bool IsThrowGetTimeoutException { get; set; } = true;
        public bool IsAutoDisposeWithSystem { get; set; } = true;
        public int CheckAvailableInterval { get; set; } = 5;

        public string ConnectionString
        {
            get => _connectionStringBuilder.ToString();
            set
            {
                _connectionStringBuilder = value;

                if (_connectionStringBuilder.MinPoolSize > 0)
                    PrevReheatConnectionPool(_pool, _connectionStringBuilder.MinPoolSize);
            }
        }

        public bool OnCheckAvailable(Object<RedisClient> obj)
        {
            obj.ResetValue();
            return obj.Value.Ping() == "PONG";
        }

        public RedisClient OnCreate()
        {
            return new RedisClient(_connectionStringBuilder.Host, _connectionStringBuilder.Ssl, _connectionStringBuilder.ConnectTimeout, 
                _connectionStringBuilder.ReceiveTimeout, _connectionStringBuilder.SendTimeout, cli => Connected(cli, new EventArgs()));
        }

        public void OnDestroy(RedisClient obj)
        {
            if (obj != null)
            {
                //if (obj.IsConnected) try { obj.Quit(); } catch { } 此行会导致，服务器主动断开后，执行该命令超时停留10-20秒
                try { obj.Dispose(); } catch { }
            }
        }

        public void OnReturn(Object<RedisClient> obj) { }

        public void OnGet(Object<RedisClient> obj)
        {
            if (_pool.IsAvailable)
            {
                if (DateTime.Now.Subtract(obj.LastReturnTime).TotalSeconds > 60 || obj.Value._singleRedisSocket.IsConnected == false)
                {
                    try
                    {
                        obj.Value.Ping();
                    }
                    catch
                    {
                        obj.ResetValue();
                    }
                }
            }
        }
#if net40
#else
        async public Task OnGetAsync(Object<RedisClient> obj)
        {
            if (_pool.IsAvailable)
            {
                if (DateTime.Now.Subtract(obj.LastReturnTime).TotalSeconds > 60 || obj.Value._singleRedisSocket.IsConnected == false)
                {
                    try
                    {
                        //await obj.Value.PingAsync();
                        await Task.FromResult(obj.Value.Ping()); //todo: async
                    }
                    catch
                    {
                        obj.ResetValue();
                    }
                }
            }
        }
#endif

        public void OnGetTimeout() { }
        public void OnAvailable() { }
        public void OnUnavailable() { }

        public static void PrevReheatConnectionPool(ObjectPool<RedisClient> pool, int minPoolSize)
        {
            if (minPoolSize <= 0) minPoolSize = Math.Min(5, pool.Policy.PoolSize);
            if (minPoolSize > pool.Policy.PoolSize) minPoolSize = pool.Policy.PoolSize;
            var initTestOk = true;
            var initStartTime = DateTime.Now;
            var initConns = new ConcurrentBag<Object<RedisClient>>();

            try
            {
                var conn = pool.Get();
                initConns.Add(conn);
                pool.Policy.OnCheckAvailable(conn);
            }
            catch (Exception ex)
            {
                initTestOk = false; //预热一次失败，后面将不进行
                pool.SetUnavailable(ex);
            }
            for (var a = 1; initTestOk && a < minPoolSize; a += 10)
            {
                if (initStartTime.Subtract(DateTime.Now).TotalSeconds > 3) break; //预热耗时超过3秒，退出
                var b = Math.Min(minPoolSize - a, 10); //每10个预热
                var initTasks = new Task[b];
                for (var c = 0; c < b; c++)
                {
                    initTasks[c] = TaskEx.Run(() =>
                    {
                        try
                        {
                            var conn = pool.Get();
                            initConns.Add(conn);
                            pool.Policy.OnCheckAvailable(conn);
                        }
                        catch
                        {
                            initTestOk = false;  //有失败，下一组退出预热
                        }
                    });
                }
                Task.WaitAll(initTasks);
            }
            while (initConns.TryTake(out var conn)) pool.Return(conn);
        }
    }
}
