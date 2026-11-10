// Guards the intended input and response shapes (zod) of the sheet set, sheet and plan/profile sheet tool actions.
// The schemas are the REAL ones from src/tools/domains/planProductionDomain.ts: input through each alias tool as a
// call is validated, responses as domainRuntime.ts checks them (tests/helpers/realSchemas.ts). plan_profile_sheet_create
// is in no tool, so its action schema is used directly. Pure schema tests: no Civil 3D, no plugin.

import { describe, it, expect } from "vitest";
import { PLAN_PRODUCTION_DOMAIN_DEFINITION as PLAN } from "../src/tools/domains/planProductionDomain.js";
import { actionInput, actionResponse, toolInput } from "./helpers/realSchemas.js";

const SheetSetCreateInputSchema = toolInput("civil3d_sheet_set_create");
const SheetSetGetInfoInputSchema = toolInput("civil3d_sheet_set_get_info");
const SheetAddInputSchema = toolInput("civil3d_sheet_add");
const SheetGetPropertiesInputSchema = toolInput("civil3d_sheet_get_properties");
const SheetSetTitleBlockInputSchema = toolInput("civil3d_sheet_set_title_block");
const PlanProfileSheetCreateInputSchema = actionInput(PLAN, "plan_profile_sheet_create");
const PlanProfileSheetUpdateAlignmentInputSchema = toolInput("civil3d_plan_profile_sheet_update_alignment");
const SheetViewCreateInputSchema = toolInput("civil3d_sheet_view_create");
const SheetViewSetScaleInputSchema = toolInput("civil3d_sheet_view_set_scale");
const PublishPdfInputSchema = toolInput("civil3d_sheet_publish_pdf");
const ExportSheetSetInputSchema = toolInput("civil3d_sheet_set_export");

const SheetSetListResponseSchema = actionResponse(PLAN, "sheet_set_list");
const SheetSetInfoResponseSchema = actionResponse(PLAN, "sheet_set_get_info");
const SheetSetCreateResponseSchema = actionResponse(PLAN, "sheet_set_create");
const SheetAddResponseSchema = actionResponse(PLAN, "sheet_add");
const SheetDetailSchema = actionResponse(PLAN, "sheet_get_properties");
const SheetSetTitleBlockResponseSchema = actionResponse(PLAN, "sheet_set_title_block");
const PlanProfileSheetCreateResponseSchema = actionResponse(PLAN, "plan_profile_sheet_create");
const SheetViewCreateResponseSchema = actionResponse(PLAN, "sheet_view_create");
const SheetViewSetScaleResponseSchema = actionResponse(PLAN, "sheet_view_set_scale");
const PublishPdfResponseSchema = actionResponse(PLAN, "sheet_publish_pdf");
const ExportSheetSetResponseSchema = actionResponse(PLAN, "sheet_set_export");

// ---------------------------------------------------------------------------
// Schema validation tests
// ---------------------------------------------------------------------------

describe("civil3d_plan_production — schema validation", () => {
  // ------ Sheet set list ------
  describe("civil3d_sheet_set_list", () => {
    it("validates a list response with one sheet set", () => {
      const data = {
        searched: ["C:/Plans"],
        sheetSets: [
          { path: "C:/Plans/Road Plans.dst", name: "Road Plans", description: "Main road plan set", sheetCount: 5 },
        ],
      };
      expect(() => SheetSetListResponseSchema.parse(data)).not.toThrow();
    });

    it("validates an empty sheet set list", () => {
      const data = { searched: [], sheetSets: [] };
      expect(() => SheetSetListResponseSchema.parse(data)).not.toThrow();
    });

    it("validates nullable description and a set that couldn't be read", () => {
      const data = {
        searched: ["C:/Plans"],
        sheetSets: [
          { path: "C:/Plans/Plans.dst", name: "Plans", description: null, sheetCount: 0 },
          { path: "C:/Plans/Broken.dst", error: "locked" },
        ],
      };
      expect(() => SheetSetListResponseSchema.parse(data)).not.toThrow();
    });

    it("rejects a sheet set without its path", () => {
      expect(() => SheetSetListResponseSchema.parse({ searched: [], sheetSets: [{ name: "Plans" }] })).toThrow();
    });
  });

  // ------ Sheet set get info ------
  describe("civil3d_sheet_set_get_info", () => {
    it("validates input with name", () => {
      expect(() => SheetSetGetInfoInputSchema.parse({ name: "Road Plans" })).not.toThrow();
    });

    it("validates response with sheets", () => {
      const data = {
        name: "Road Plans",
        handle: "A1",
        description: null,
        sheets: [
          { name: "Sheet 1", number: "C-01", handle: "B2", layoutName: "Layout1" },
          { name: "Sheet 2", number: "C-02", handle: "B3", layoutName: null },
        ],
      };
      expect(() => SheetSetInfoResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Sheet set create ------
  describe("civil3d_sheet_set_create", () => {
    it("validates minimal create input", () => {
      expect(() => SheetSetCreateInputSchema.parse({ name: "New Set", outputPath: "C:/Plans/New Set.dst" })).not.toThrow();
    });

    it("validates create input with description, layouts and overwrite", () => {
      expect(() =>
        SheetSetCreateInputSchema.parse({
          name: "New Set", outputPath: "C:/Plans/New Set.dst", description: "A plan set", layoutNames: ["C-101"], overwrite: true,
        })
      ).not.toThrow();
    });

    it("requires the .dst outputPath", () => {
      expect(() => SheetSetCreateInputSchema.parse({ name: "New Set" })).toThrow();
    });

    it("validates create response", () => {
      const data = { name: "New Set", handle: "C3", created: true };
      expect(() => SheetSetCreateResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Add sheet ------
  describe("civil3d_sheet_add", () => {
    it("validates minimal add sheet input", () => {
      expect(() =>
        SheetAddInputSchema.parse({ sheetSetName: "Road Plans", sheetName: "Sheet 1" })
      ).not.toThrow();
    });

    it("validates add sheet input with all optional fields", () => {
      expect(() =>
        SheetAddInputSchema.parse({
          sheetSetName: "Road Plans",
          sheetName: "Sheet 1",
          sheetNumber: "C-01",
          layoutName: "Layout1",
        })
      ).not.toThrow();
    });

    it("validates add sheet response", () => {
      const data = { name: "Sheet 1", number: "C-01", handle: "D4", added: true };
      expect(() => SheetAddResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Get sheet properties ------
  describe("civil3d_sheet_get_properties", () => {
    it("validates input", () => {
      expect(() =>
        SheetGetPropertiesInputSchema.parse({ sheetSetName: "Road Plans", sheetName: "Sheet 1" })
      ).not.toThrow();
    });

    it("validates full detail response", () => {
      const data = {
        name: "Sheet 1",
        number: "C-01",
        title: "Plan and Profile",
        layout: "Layout1",
        drawing: "C:/Plans/C-101.dwg",
        doNotPlot: false,
      };
      expect(() => SheetDetailSchema.parse(data)).not.toThrow();
    });

    it("validates response with all nullable fields null", () => {
      const data = {
        name: "Sheet 1",
        number: "",
        title: null,
        layout: null,
        drawing: null,
        doNotPlot: true,
      };
      expect(() => SheetDetailSchema.parse(data)).not.toThrow();
    });

    it("rejects a response without doNotPlot", () => {
      expect(() => SheetDetailSchema.parse({ name: "Sheet 1", number: "C-01", title: null, layout: null, drawing: null })).toThrow();
    });
  });

  // ------ Set title block ------
  describe("civil3d_sheet_set_title_block", () => {
    it("validates input", () => {
      expect(() =>
        SheetSetTitleBlockInputSchema.parse({
          sheetSetName: "Road Plans",
          sheetName: "Sheet 1",
          titleBlockPath: "C:/templates/title.dwg",
        })
      ).not.toThrow();
    });

    it("validates response", () => {
      const data = { sheetName: "Sheet 1", titleBlock: "C:/templates/title.dwg", updated: true };
      expect(() => SheetSetTitleBlockResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Plan/profile sheet create ------
  describe("civil3d_plan_profile_sheet_create", () => {
    it("validates minimal input (alignment only)", () => {
      expect(() =>
        PlanProfileSheetCreateInputSchema.parse({
          sheetSetName: "Road Plans",
          alignmentName: "Main Road",
        })
      ).not.toThrow();
    });

    it("validates full input", () => {
      expect(() =>
        PlanProfileSheetCreateInputSchema.parse({
          sheetSetName: "Road Plans",
          alignmentName: "Main Road",
          profileName: "EG Profile",
          sheetTemplatePath: "C:/templates/plan-profile.dwt",
          startStation: 0.0,
          endStation: 500.0,
          viewScale: 50,
        })
      ).not.toThrow();
    });

    it("validates response", () => {
      const data = {
        sheetName: "Main Road Plan/Profile",
        handle: "G7",
        alignmentName: "Main Road",
        profileName: "EG Profile",
        created: true,
      };
      expect(() => PlanProfileSheetCreateResponseSchema.parse(data)).not.toThrow();
    });

    it("validates response with null profileName", () => {
      const data = {
        sheetName: "Main Road Plan",
        handle: "H8",
        alignmentName: "Main Road",
        profileName: null,
        created: true,
      };
      expect(() => PlanProfileSheetCreateResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Update alignment ------
  describe("civil3d_plan_profile_sheet_update_alignment", () => {
    it("validates input", () => {
      expect(() =>
        PlanProfileSheetUpdateAlignmentInputSchema.parse({
          sheetSetName: "Road Plans",
          sheetName: "Sheet 1",
          alignmentName: "New Road",
        })
      ).not.toThrow();
    });

    it("validates input with profileName", () => {
      expect(() =>
        PlanProfileSheetUpdateAlignmentInputSchema.parse({
          sheetSetName: "Road Plans",
          sheetName: "Sheet 1",
          alignmentName: "New Road",
          profileName: "Design Profile",
        })
      ).not.toThrow();
    });
  });

  // ------ Sheet view create ------
  describe("civil3d_sheet_view_create", () => {
    it("validates minimal input (layout name only)", () => {
      expect(() =>
        SheetViewCreateInputSchema.parse({ layoutName: "Layout1" })
      ).not.toThrow();
    });

    it("validates full input", () => {
      expect(() =>
        SheetViewCreateInputSchema.parse({
          layoutName: "Layout1",
          viewName: "Main View",
          centerX: 12.0,
          centerY: 8.5,
          width: 24.0,
          height: 18.0,
          scale: 100,
        })
      ).not.toThrow();
    });

    it("validates response", () => {
      const data = { handle: "I9", layoutName: "Layout1", scale: 100, created: true };
      expect(() => SheetViewCreateResponseSchema.parse(data)).not.toThrow();
    });

    it("validates response with null scale", () => {
      const data = { handle: "J10", layoutName: "Layout1", scale: null, created: true };
      expect(() => SheetViewCreateResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Sheet view set scale ------
  describe("civil3d_sheet_view_set_scale", () => {
    it("validates input", () => {
      expect(() =>
        SheetViewSetScaleInputSchema.parse({ layoutName: "Layout1", scale: 50 })
      ).not.toThrow();
    });

    it("validates input with viewport handle", () => {
      expect(() =>
        SheetViewSetScaleInputSchema.parse({ layoutName: "Layout1", viewportHandle: "K11", scale: 100 })
      ).not.toThrow();
    });

    it("rejects non-positive scale", () => {
      expect(() =>
        SheetViewSetScaleInputSchema.parse({ layoutName: "Layout1", scale: -10 })
      ).toThrow();
    });

    it("validates response", () => {
      const data = { handle: "L12", scale: 50, updated: true };
      expect(() => SheetViewSetScaleResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Publish PDF ------
  describe("civil3d_sheet_publish_pdf", () => {
    it("validates minimal input", () => {
      expect(() =>
        PublishPdfInputSchema.parse({
          layoutNames: ["Layout1"],
          outputPath: "C:/output/sheets.pdf",
        })
      ).not.toThrow();
    });

    it("validates input with multiple layouts and options", () => {
      expect(() =>
        PublishPdfInputSchema.parse({
          layoutNames: ["Layout1", "Layout2", "Layout3"],
          outputPath: "C:/output/sheets.pdf",
          plotStyleTable: "monochrome.ctb",
          paperSize: "ARCH D (24.00 x 36.00 Inches)",
        })
      ).not.toThrow();
    });

    it("rejects empty layoutNames array", () => {
      expect(() =>
        PublishPdfInputSchema.parse({ layoutNames: [], outputPath: "C:/out.pdf" })
      ).toThrow();
    });

    it("validates response", () => {
      const data = { outputPath: "C:/output/sheets.pdf", sheetsPublished: 3, published: true };
      expect(() => PublishPdfResponseSchema.parse(data)).not.toThrow();
    });
  });

  // ------ Export sheet set ------
  describe("civil3d_sheet_set_export", () => {
    it("validates minimal input", () => {
      expect(() =>
        ExportSheetSetInputSchema.parse({
          sheetSetName: "Road Plans",
          outputPath: "C:/output/road-plans.pdf",
        })
      ).not.toThrow();
    });

    it("validates input with plot style table", () => {
      expect(() =>
        ExportSheetSetInputSchema.parse({
          sheetSetName: "Road Plans",
          outputPath: "C:/output/road-plans.pdf",
          plotStyleTable: "monochrome.ctb",
        })
      ).not.toThrow();
    });

    it("validates response", () => {
      const data = {
        sheetSetName: "Road Plans",
        outputPath: "C:/output/road-plans.pdf",
        sheetsExported: 12,
        exported: true,
      };
      expect(() => ExportSheetSetResponseSchema.parse(data)).not.toThrow();
    });
  });
});

