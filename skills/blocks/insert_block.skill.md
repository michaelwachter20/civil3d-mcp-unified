---
name: insert_block
category: blocks
description: Insert a block reference at a point with rotation, scale, and attribute values
requires_write: true
parameters:
  - name: blockName
    type: string
    required: true
    description: Name of a block already defined in the drawing
  - name: insertionPoint
    type: point
    required: true
    description: "{x, y, z}"
  - name: rotationDegrees
    type: number
    required: false
  - name: scale
    type: number
    required: false
  - name: attributes
    type: json
    required: false
    description: Object of TAG -> value, e.g. {"STATION":"10+00"}
---

## Code Template (civil3d_execute)

```csharp
var blockName = Args.GetString("blockName");
var point = Args.GetPoint("insertionPoint");
var rotation = Args.GetDouble("rotationDegrees", 0) * Math.PI / 180.0;
var scale = Args.GetDouble("scale", 1.0);
var values = Args.Get<Dictionary<string, string>>("attributes") ?? new Dictionary<string, string>();

var blockTable = (BlockTable)Transaction.GetObject(Database.BlockTableId, OpenMode.ForRead);
if (!blockTable.Has(blockName))
{
    return new { error = $"Block '{blockName}' is not defined in this drawing." };
}

var definitionId = blockTable[blockName];
var reference = new BlockReference(point, definitionId)
{
    Rotation = rotation,
    ScaleFactors = new Scale3d(scale),
};
AddToModelSpace(reference);

var definition = (BlockTableRecord)Transaction.GetObject(definitionId, OpenMode.ForRead);
var applied = new List<string>();
if (definition.HasAttributeDefinitions)
{
    foreach (ObjectId id in definition)
    {
        if (Transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attributeDefinition && !attributeDefinition.Constant)
        {
            var attribute = new AttributeReference();
            attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
            if (values.TryGetValue(attributeDefinition.Tag, out var text))
            {
                attribute.TextString = text;
                applied.Add(attributeDefinition.Tag);
            }
            reference.AttributeCollection.AppendAttribute(attribute);
            Transaction.AddNewlyCreatedDBObject(attribute, true);
        }
    }
}

return new { handle = reference.Handle.ToString(), blockName, applied };
```

## Usage Notes
- Attribute tags are case-sensitive as defined in the block.
- To insert many blocks, loop over an array argument and call the body once per point.
