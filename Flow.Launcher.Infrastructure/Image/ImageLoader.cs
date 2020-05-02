using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Flow.Launcher.Infrastructure.Logger;
using Flow.Launcher.Infrastructure.Storage;

namespace Flow.Launcher.Infrastructure.Image
{
    public static class ImageLoader
    {
        private static readonly ImageCache _imageCache = new ImageCache();
        private static readonly ConcurrentDictionary<string, string> _guidToKey = new ConcurrentDictionary<string, string>();
        private static readonly bool _enableHashImage = true;

        private static BinaryStorage<Dictionary<string, int>> _storage;
        private static IImageHashGenerator _hashGenerator;

        private static readonly string[] ImageExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".tiff",
            ".ico"
        };

        public static void Initialize()
        {
            _storage = new BinaryStorage<Dictionary<string, int>>("Image");
            _hashGenerator = new ImageHashGenerator();

            _imageCache.Usage = LoadStorageToConcurrentDictionary();

            foreach (var icon in new[] { Constant.DefaultIcon, Constant.ErrorIcon })
            {
                ImageSource img = new BitmapImage(new Uri(icon));
                img.Freeze();
                _imageCache[icon] = img;
            }

            Task.Run(() =>
            {
                Stopwatch.Normal("|ImageLoader.Initialize|Preload images cost", () =>
                {
                    _imageCache.Usage.AsParallel().ForAll(x =>
                    {
                        Load(x.Key);
                    });
                });
                Log.Info($"|ImageLoader.Initialize|Number of preload images is <{_imageCache.Usage.Count}>, Images Number: {_imageCache.CacheSize()}, Unique Items {_imageCache.UniqueImagesInCache()}");
            });
        }

        public static void Save()
        {
            lock (_storage)
            {
                _storage.Save(_imageCache.CleanupAndToDictionary());
            }
        }

        private static ConcurrentDictionary<string, int> LoadStorageToConcurrentDictionary()
        {
            lock(_storage)
            {
                var loaded = _storage.TryLoad(new Dictionary<string, int>());

                return new ConcurrentDictionary<string, int>(loaded);
            }
        }

        private class ImageResult
        {
            public ImageResult(ImageSource imageSource, ImageType imageType)
            {
                ImageSource = imageSource;
                ImageType = imageType;
            }

            public ImageType ImageType { get; }
            public ImageSource ImageSource { get; }
        }

        private enum ImageType
        {
            File,
            Folder,
            Data,
            ImageFile,
            Error,
            Cache
        }

        private static ImageResult LoadInternal(string path, bool loadFullImage = false)
        {
            ImageSource image;
            ImageType type = ImageType.Error;
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return new ImageResult(_imageCache[Constant.ErrorIcon], ImageType.Error);
                }
                if (_imageCache.ContainsKey(path))
                {
                    return new ImageResult(_imageCache[path], ImageType.Cache);
                }

                if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var imageSource = new BitmapImage(new Uri(path));
                    imageSource.Freeze();
                    return new ImageResult(imageSource, ImageType.Data);
                }

                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(Constant.ProgramDirectory, "Images", Path.GetFileName(path));
                }

                if (Directory.Exists(path))
                {
                    /* Directories can also have thumbnails instead of shell icons.
                     * Generating thumbnails for a bunch of folders while scrolling through
                     * results from Everything makes a big impact on performance and 
                     * Flow.Launcher responsibility. 
                     * - Solution: just load the icon
                     */
                    type = ImageType.Folder;
                    image = WindowsThumbnailProvider.GetThumbnail(
                        path, 
                        Constant.ThumbnailSize,
                        Constant.ThumbnailSize, 
                        ThumbnailOptions.IconOnly);

                }
                else if (File.Exists(path))
                {
                    var extension = Path.GetExtension(path).ToLower();
                    if (ImageExtensions.Contains(extension))
                    {
                        type = ImageType.ImageFile;
                        if (loadFullImage)
                        {
                            image = LoadFullImage(path);
                        }
                        else
                        {
                            /* Although the documentation for GetImage on MSDN indicates that 
                             * if a thumbnail is available it will return one, this has proved to not
                             * be the case in many situations while testing. 
                             * - Solution: explicitly pass the ThumbnailOnly flag
                             */
                            image = WindowsThumbnailProvider.GetThumbnail(
                                path, 
                                Constant.ThumbnailSize,
                                Constant.ThumbnailSize, 
                                ThumbnailOptions.ThumbnailOnly);
                        }
                    }
                    else
                    {
                        type = ImageType.File;
                        image = WindowsThumbnailProvider.GetThumbnail(
                            path, 
                            Constant.ThumbnailSize,
                            Constant.ThumbnailSize, 
                            ThumbnailOptions.ThumbnailOnly);
                    }
                }
                else
                {
                    image = _imageCache[Constant.ErrorIcon];
                    path = Constant.ErrorIcon;
                }

                if (type != ImageType.Error)
                {
                    image.Freeze();
                }
            }
            catch (System.Exception e)
            {
                Log.Exception($"|ImageLoader.Load|Failed to get thumbnail for {path}", e);
                type = ImageType.Error;
                image = _imageCache[Constant.ErrorIcon];
                _imageCache[path] = image;
            }

            return new ImageResult(image, type);
        }

        public static ImageSource Load(string path, bool loadFullImage = false)
        {
            var imageResult = LoadInternal(path, loadFullImage);

            var img = imageResult.ImageSource;
            if (imageResult.ImageType != ImageType.Error && imageResult.ImageType != ImageType.Cache)
            { 
                // we need to get image hash
                string hash = _enableHashImage ? _hashGenerator.GetHashFromImage(img) : null;
                if (hash != null)
                {
                    if (_guidToKey.TryGetValue(hash, out string key))
                    { 
                        // image already exists
                        img = _imageCache[key];
                    }
                    else
                    { 
                        // new guid
                        _guidToKey[hash] = path;
                    }
                }

                // update cache
                _imageCache[path] = img;
            }

            return img;
        }

        private static BitmapImage LoadFullImage(string path)
        {
            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            return image;
        }
    }
}
