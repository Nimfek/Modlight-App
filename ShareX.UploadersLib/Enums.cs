#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.ComponentModel;

namespace ShareX.UploadersLib
{
    [Description("Image uploaders"), DefaultValue(Modlight)]
    public enum ImageDestination
    {
        [Description("Modlight")]
        Modlight,
        FileUploader // Localized
    }

    [Description("Text uploaders"), DefaultValue(Modlight)]
    public enum TextDestination
    {
        [Description("Modlight")]
        Modlight,
        FileUploader // Localized
    }

    [Description("File uploaders"), DefaultValue(Modlight)]
    public enum FileDestination
    {
        [Description("Modlight")]
        Modlight
    }

    [Description("URL shorteners"), DefaultValue(Modlight)]
    public enum UrlShortenerType
    {
        [Description("Modlight")]
        Modlight
    }

    [Description("URL sharing services"), DefaultValue(GoogleImageSearch)]
    public enum URLSharingServices
    {
        GoogleImageSearch, // Localized
        BingVisualSearch // Localized
    }

    public enum HttpMethod
    {
        GET,
        POST,
        PUT,
        PATCH,
        DELETE
    }

    public enum LinkFormatEnum
    {
        [Description("Full URL")]
        URL,
        [Description("Full Image for Forums")]
        ForumImage,
        [Description("Full Image as HTML")]
        HTMLImage,
        [Description("Full Image for Wiki")]
        WikiImage,
        [Description("Shortened URL")]
        ShortenedURL,
        [Description("Linked Thumbnail for Forums")]
        ForumLinkedImage,
        [Description("Linked Thumbnail as HTML")]
        HTMLLinkedImage,
        [Description("Linked Thumbnail for Wiki")]
        WikiLinkedImage,
        [Description("Thumbnail")]
        ThumbnailURL,
        [Description("Local File path")]
        LocalFilePath,
        [Description("Local File path as URI")]
        LocalFilePathUri
    }

}