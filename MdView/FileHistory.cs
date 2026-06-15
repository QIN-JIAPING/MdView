using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MdView
{
    public class FileHistory
    {
        private const int MaxEntries = 15;
        private static readonly string StoragePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdView", "history.json");

        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Entries => _entries.AsReadOnly();

        public event Action? Changed;

        public static FileHistory Load()
        {
            var history = new FileHistory();
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(StoragePath))
                {
                    var json = File.ReadAllText(StoragePath);
                    var list = JsonSerializer.Deserialize<List<string>>(json);
                    if (list != null)
                    {
                        foreach (var p in list)
                        {
                            if (File.Exists(p) && !history._entries.Contains(p))
                                history._entries.Add(p);
                        }
                    }
                }
            }
            catch { }
            return history;
        }

        public void Add(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch { return; }

            _entries.Remove(path);
            _entries.Insert(0, path);

            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(_entries.Count - 1);

            Save();
            Changed?.Invoke();
        }

        public void Remove(string path)
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch { return; }

            _entries.Remove(path);
            Save();
            Changed?.Invoke();
        }

        public void Clear()
        {
            _entries.Clear();
            Save();
            Changed?.Invoke();
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(StoragePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StoragePath, json);
            }
            catch { }
        }
    }
}
