using LibNDSFormats.NSBMD;
using MKDS_Course_Editor.Export3DTools;
using Tao.OpenGl;
using DSPRE.ROMFiles;

namespace DSPRE.Rendering;

/// <summary>
/// Reusable fixed-function renderer for Gen IV map models.
/// The caller owns the OpenGL context and must make it current before rendering.
/// </summary>
public sealed class LegacyMap3DRenderer
{
    private readonly NSBMDGlRenderer mapRenderer = new NSBMDGlRenderer();
    private readonly NSBMDGlRenderer buildingsRenderer = new NSBMDGlRenderer();

    public bool HideBuildings { get; set; }

    public void Render(MapFile mapFile, float angle, float distance, float elevation, float perspective,
        int width, int height, bool mapTextures = true, bool buildingTextures = true)
    {
        if (mapFile?.mapModel?.models is null || mapFile.mapModel.models.Length == 0)
        {
            return;
        }

        SetupScene(angle, distance, elevation, perspective, width, height);

            MKDS_Course_Editor.NSBTA.NSBTA.NSBTA_File animation = new MKDS_Course_Editor.NSBTA.NSBTA.NSBTA_File();
            MKDS_Course_Editor.NSBTP.NSBTP.NSBTP_File texturePattern = new MKDS_Course_Editor.NSBTP.NSBTP.NSBTP_File();
            MKDS_Course_Editor.NSBCA.NSBCA.NSBCA_File cameraAnimation = new MKDS_Course_Editor.NSBCA.NSBCA.NSBCA_File();
        int[] emptyFrames = [];

        mapRenderer.Model = mapFile.mapModel.models[0];
        float mapScale = mapFile.mapModel.models[0].modelScale / 64f;
        Gl.glScalef(mapScale, mapScale, mapScale);
        SetTexturesEnabled(mapTextures);
        mapRenderer.RenderModel("", animation, emptyFrames, emptyFrames, emptyFrames, emptyFrames, emptyFrames,
            cameraAnimation, false, -1, 0f, 0f, distance, elevation, angle, true, texturePattern, mapFile.mapModel);

        if (HideBuildings)
        {
            return;
        }

        SetTexturesEnabled(buildingTextures);
        foreach (Building building in mapFile.buildings ?? [])
        {
            if (building.NSBMDFile?.models is null || building.NSBMDFile.models.Length == 0)
            {
                continue;
            }

            buildingsRenderer.Model = building.NSBMDFile.models[0];
            ScaleTranslateRotateBuilding(building);
            buildingsRenderer.RenderModel("", animation, emptyFrames, emptyFrames, emptyFrames, emptyFrames, emptyFrames,
                cameraAnimation, false, -1, 0f, 0f, distance, elevation, angle, true, texturePattern, building.NSBMDFile);
        }
    }

    public static void SetupScene(float angle, float distance, float elevation, float perspective, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Gl.glEnable(Gl.GL_RESCALE_NORMAL);
        Gl.glEnable(Gl.GL_COLOR_MATERIAL);
        Gl.glEnable(Gl.GL_DEPTH_TEST);
        Gl.glEnable(Gl.GL_NORMALIZE);
        Gl.glDisable(Gl.GL_CULL_FACE);
        Gl.glFrontFace(Gl.GL_CCW);
        Gl.glClearDepth(1);
        Gl.glEnable(Gl.GL_ALPHA_TEST);
        Gl.glBlendFunc(Gl.GL_SRC_ALPHA, Gl.GL_ONE_MINUS_SRC_ALPHA);
        Gl.glEnable(Gl.GL_BLEND);
        Gl.glAlphaFunc(Gl.GL_GREATER, 0f);
        Gl.glClearColor(51f / 255f, 51f / 255f, 51f / 255f, 1f);
        Gl.glViewport(0, 0, width, height);

        Gl.glMatrixMode(Gl.GL_PROJECTION);
        Gl.glLoadIdentity();
        Glu.gluPerspective(perspective, (float)width / height, 0.2f, 500f);
        Gl.glMatrixMode(Gl.GL_MODELVIEW);
        Gl.glLoadIdentity();
        Gl.glTranslatef(0, 0, -distance);
        Gl.glRotatef(elevation, 1, 0, 0);
        Gl.glRotatef(-angle, 0, 1, 0);
        Gl.glLightfv(Gl.GL_LIGHT0, Gl.GL_POSITION, [1, 1, 1, 0]);
        Gl.glLightfv(Gl.GL_LIGHT1, Gl.GL_POSITION, [1, 1, 1, 0]);
        Gl.glLightfv(Gl.GL_LIGHT2, Gl.GL_POSITION, [1, 1, 1, 0]);
        Gl.glLightfv(Gl.GL_LIGHT3, Gl.GL_POSITION, [1, 1, 1, 0]);
        Gl.glBindTexture(Gl.GL_TEXTURE_2D, 0);
        Gl.glColor3f(1f, 1f, 1f);
        Gl.glDepthMask(Gl.GL_TRUE);
        Gl.glClear(Gl.GL_COLOR_BUFFER_BIT | Gl.GL_DEPTH_BUFFER_BIT);
    }

    private static void SetTexturesEnabled(bool enabled)
    {
        if (enabled) Gl.glEnable(Gl.GL_TEXTURE_2D);
        else Gl.glDisable(Gl.GL_TEXTURE_2D);
    }

    private static void ScaleTranslateRotateBuilding(Building building)
    {
        float fullX = building.xPosition + building.xFraction / 65536f;
        float fullY = building.yPosition + building.yFraction / 65536f;
        float fullZ = building.zPosition + building.zFraction / 65536f;
        float scaleFactor = building.NSBMDFile.models[0].modelScale / 1024f;
        float translateFactor = 256f / building.NSBMDFile.models[0].modelScale;

        Gl.glScalef(scaleFactor * building.width, scaleFactor * building.height, scaleFactor * building.length);
        Gl.glTranslatef(fullX * translateFactor / building.width, fullY * translateFactor / building.height, fullZ * translateFactor / building.length);
        Gl.glRotatef(Building.U16ToDeg(building.zRotation), 0, 0, 1);
        Gl.glRotatef(Building.U16ToDeg(building.yRotation), 0, 1, 0);
        Gl.glRotatef(Building.U16ToDeg(building.xRotation), 1, 0, 0);
    }
}